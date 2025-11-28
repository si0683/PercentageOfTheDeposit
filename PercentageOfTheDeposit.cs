using OsEngine.Charts.CandleChart.Indicators;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.Market.Servers.Transaq.TransaqEntity;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms.DataVisualization.Charting;
using TL;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using static Tinkoff.InvestApi.V1.OrderStateStreamResponse.Types;
using Candle = OsEngine.Entity.Candle;
namespace OsEngine.Robots.PercentageOfTheDeposit
{
    namespace OsEngine.Custom.PercentageOfTheDeposit
    {
        [Bot("PercentageOfTheDeposit")]

        public class PercentageOfTheDeposit : BotPanel
        {

            BotTabSimple _tab;

            // Basic settings
            private StrategyParameterString _tradeMode;
            private StrategyParameterString _regime;

            // GetVolume settings
            private StrategyParameterString _volumeType;
            private StrategyParameterDecimal _volume;
            private StrategyParameterDecimal _slippagePercent;
            private StrategyParameterDecimal _feePercent;
            private StrategyParameterDecimal _stopPercent;




            public PercentageOfTheDeposit(string name, StartProgram startProgram) : base(name, startProgram)
            {
                TabCreate(BotTabType.Simple);   // создаём Simple вкладку
                _tab = TabsSimple[0];           // присваиваем её в _tab


                // Basic settings
                //_tradeAssetInPortfolio = CreateParameter("Asset in portfolio", "Prime"); ДЛЯ ТЕСТЕРА, ВНИЗУ МЕТОД РАСКОММЕНТИРОВАТЬ!!
                // GetVolume settings
                _volumeType = CreateParameter("Тип объёма", "Deposit percent");
                _volume = CreateParameter("Риск на сделку %", 1m, 1.0m, 50m, 4m);
                _stopPercent = CreateParameter("Процент стопа SL %", 0.25m, 0.1m, 50m, 0.1m);
                _slippagePercent = CreateParameter("Проскальзывание (%)", 0.1m, 0.01m, 2m, 0.01m);
                _feePercent = CreateParameter("Комиссия за сделку (%)", 0.1m, 0.01m, 1m, 0.01m);







            }



            public override string GetNameStrategyType()
            {
                return "PercentageOfTheDeposit";
            }


            private decimal GetVolume(BotTabSimple tab)
            {
                // ===========================
                // 0) Проверка режима и портфеля
                // ===========================
                if (_volumeType.ValueString != "Deposit percent")
                    return 0;

                if (tab?.Portfolio == null)
                {
                    SendNewLogMessage("Портфель null — невозможно рассчитать объём", Logging.LogMessageType.Error);
                    return 0;
                }

                Portfolio portfolio = tab.Portfolio;

                // ===========================
                // 1) Определяем баланс
                // ===========================
                string secName = (tab.Security.Name ?? "").ToUpper();
                string baseSymbol = secName.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)[0];

                string requiredCurrency = DetectRequiredCurrency(secName);

                // КРИПТА: баланс
                decimal balance = GetCryptoBalance(portfolio, requiredCurrency);

                // МОЕХ определяется только по бирже
                bool isMoex = (tab.Security.Exchange ?? "")
                                .ToUpper()
                                .Contains("MOEX");

                if (isMoex)
                    balance = GetMoexBalance(portfolio);

                // Fallback — если ничего не нашли
                if (balance <= 0)
                {
                    balance = portfolio.ValueCurrent;

                    SendNewLogMessage(
                        $"Валюта {requiredCurrency} не найдена. Fallback ValueCurrent: {balance}",
                        Logging.LogMessageType.System);
                }

                // ===========================
                // 2) Определяем тип инструмента
                // ===========================
                bool isInverseFuture =
                       baseSymbol == "BTCUSD"
                    || baseSymbol == "ETHUSD"
                    || baseSymbol == "XRPUSD"
                    || baseSymbol == "SOLUSD"
                    || baseSymbol == "ADAUSD"
                    || baseSymbol == "LTCUSD"
                    || baseSymbol == "AVAXUSD"
                    || baseSymbol == "ETCUSD"
                    || baseSymbol == "TONUSD";

                bool isPerp = secName.EndsWith("USDT.P");
                bool isSpot = !isPerp && secName.EndsWith("USDT");   // ← БОЛЬШОЙ ФИКС

                // ===========================
                // 3) Проверка стопа и риск-параметров
                // ===========================
                decimal stopPct = _stopPercent.ValueDecimal / 100m;
                if (stopPct <= 0)
                {
                    SendNewLogMessage("STOP% <= 0 — расчёт невозможен", Logging.LogMessageType.Error);
                    return 0;
                }

                decimal slippagePct = _slippagePercent.ValueDecimal / 100m;
                decimal feePct = _feePercent.ValueDecimal / 100m;

                decimal realStopPct = stopPct + slippagePct + feePct * 2m;
                if (realStopPct <= 0)
                    realStopPct = stopPct;

                decimal riskPct = _volume.ValueDecimal / 100m;
                decimal riskMoney = balance * riskPct;
                decimal positionMoney = riskMoney / realStopPct;

                // ===========================
                // 4) Проверка цены
                // ===========================
                decimal price = tab.PriceBestAsk;
                if (price <= 0)
                {
                    SendNewLogMessage("PriceBestAsk <= 0 — расчёт невозможен", Logging.LogMessageType.Error);
                    return 0;
                }

                // ===========================
                // ЛОГИ
                // ===========================
                string log =
                    "=== Объём позиции: расчёт ===\n" +
                    $"BALANCE               = {balance}\n" +
                    $"RISK%                 = {_volume.ValueDecimal}%\n" +
                    $"STOP% (raw)           = {_stopPercent.ValueDecimal}%\n" +
                    $"SLIPPAGE%             = {_slippagePercent.ValueDecimal}%\n" +
                    $"FEE%                  = {_feePercent.ValueDecimal}% (x2)\n" +
                    $"realStopPct           = {realStopPct * 100m:F4}%\n" +
                    $"riskMoney             = {riskMoney}\n" +
                    $"positionMoney(final)  = {positionMoney}\n" +
                    $"isInverse             = {isInverseFuture}\n" +
                    $"isPerp                = {isPerp}\n" +
                    $"isSpot                = {isSpot}\n" +
                    $"isMoex                = {isMoex}\n" +
                    $"Security.Name         = {secName}\n" +
                    $"Lot                   = {tab.Security.Lot}\n" +
                    $"DecimalsVolume        = {tab.Security.DecimalsVolume}\n" +
                    "=============================";

                SendNewLogMessage(log, Logging.LogMessageType.System);

                // ===========================
                // 5) INVERSE FUTURES
                // ===========================
                if (isInverseFuture)
                {
                    decimal qty = positionMoney * price;
                    qty = AdjustQty(qty, positionMoney, tab, true);
                    return qty;
                }

                // ===========================
                // 6) SPOT / PERP / MOEX (общая формула)
                // ===========================
                if (isPerp || isSpot || isMoex)
                {
                    decimal qty = positionMoney / price / tab.Security.Lot;
                    qty = AdjustQty(qty, positionMoney, tab, false);
                    return qty;
                }

                return 0;
            }



            private decimal AdjustQty(decimal qty, decimal positionMoney, BotTabSimple tab, bool isInverse)
            {
                qty = Math.Round(qty, tab.Security.DecimalsVolume);

                if (tab.StartProgram == StartProgram.IsOsTrader && !isInverse)
                {
                    if (tab.Security.UsePriceStepCostToCalculateVolume == true
                        && tab.Security.PriceStep != tab.Security.PriceStepCost
                        && tab.PriceBestAsk != 0
                        && tab.Security.PriceStep != 0
                        && tab.Security.PriceStepCost != 0)
                    {
                        qty = positionMoney /
                              (tab.PriceBestAsk / tab.Security.PriceStep * tab.Security.PriceStepCost);
                    }

                    int dec = tab.Security.DecimalsVolume > 0 ? tab.Security.DecimalsVolume : 8;
                    qty = Math.Round(qty, dec);
                }
                else
                {
                    qty = Math.Round(qty, 7);
                }

                SendNewLogMessage("Объем денег в сделку: " + qty, Logging.LogMessageType.System);
                return qty;
            }

            private decimal GetCryptoBalance(Portfolio portfolio, string requiredCurrency)
            {
                List<PositionOnBoard> positions = portfolio.GetPositionOnBoard();
                if (positions == null || positions.Count == 0)
                    return 0;

                foreach (var pos in positions)
                {
                    string code = (pos.SecurityNameCode ?? "").ToUpper();
                    if (code != requiredCurrency)
                        continue;

                    decimal free = pos.ValueCurrent - pos.ValueBlocked;

                    SendNewLogMessage(
                        $"{code}: total={pos.ValueCurrent}, free={free}",
                        Logging.LogMessageType.System);

                    return free;
                }

                return 0;
            }



            private decimal GetMoexBalance(Portfolio portfolio)
            {
                decimal balance = 0m;

                if (portfolio == null)
                    return 0;

                var positions = portfolio.GetPositionOnBoard();
                if (positions == null)
                    return 0;

                foreach (var p in positions)
                {
                    // На MOEX баланс ВСЕГДА лежит в инструменте RUB
                    if ((p.SecurityNameCode ?? "").ToUpper() == "RUB")
                    {
                        balance = p.ValueCurrent - p.ValueBlocked;
                        break;
                    }
                }

                return balance;
            }


            private string DetectRequiredCurrency(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return "USDT";

                string symbol = raw.ToUpper()
                    .Split(' ')[0]
                    .Split('.')[0]
                    .Split('-')[0];

                // 1) сначала проверяем инверсные фьючерсы
                if (symbol == "BTCUSD")
                    return "BTC";

                if (symbol == "ETHUSD")
                    return "ETH";

                if (symbol == "XRPUSD")
                    return "XRP";

                if (symbol == "SOLUSD")
                    return "SOL";

                if (symbol == "ADAUSD")
                    return "ADA";

                if (symbol == "LTCUSD")
                    return "LTC";

                if (symbol == "AVAXUSD")
                    return "AVAX";

                if (symbol == "ETCUSD")
                    return "ETC";

                if (symbol == "TONUSD")
                    return "TON";

                // 2) пары к USDT
                if (symbol.EndsWith("USDT"))
                    return "USDT";

                if (symbol.EndsWith("RUB"))
                    return "RUB";

                // 3) обычные USD-пары (не инверсные)
                if (symbol.EndsWith("USD"))
                    return "USD";

                // 4) пары к BTC
                if (symbol.EndsWith("BTC"))
                    return "BTC";

                // 5) пары к EUR
                if (symbol.EndsWith("EUR"))
                    return "EUR";

                // 6) fallback: последние 3 символа
                if (symbol.Length > 3)
                    return symbol.Substring(symbol.Length - 3);

                return "USDT";
            }


            /* ДЛЯ ТЕСТЕРА!!!!!!!!!!!
                 private decimal GetVolume(BotTabSimple tab)
        {
            decimal volume = 0;

            if (_volumeType.ValueString == "Contracts")
            {
                volume = _volume.ValueDecimal;
            }
            else if (_volumeType.ValueString == "Contract currency")
            {
                decimal contractPrice = tab.PriceBestAsk;
                volume = _volume.ValueDecimal / contractPrice;

                if (StartProgram == StartProgram.IsOsTrader)
                {
                    IServerPermission serverPermission = ServerMaster.GetServerPermission(tab.Connector.ServerType);

                    if (serverPermission != null &&
                        serverPermission.IsUseLotToCalculateProfit &&
                    tab.Security.Lot != 0 &&
                        tab.Security.Lot > 1)
                    {
                        volume = _volume.ValueDecimal / (contractPrice * tab.Security.Lot);
                    }

                    volume = Math.Round(volume, tab.Security.DecimalsVolume);
                }
                else // Tester or Optimizer
                {
                    volume = Math.Round(volume, 6);
                }
            }
            else if (_volumeType.ValueString == "Deposit percent")
            {
                Portfolio myPortfolio = tab.Portfolio;

                if (myPortfolio == null)
                {
                    return 0;
                }

                decimal portfolioPrimeAsset = 0;

                if (_tradeAssetInPortfolio.ValueString == "Prime")
                {
                    portfolioPrimeAsset = myPortfolio.ValueCurrent;
                }
                else
                {
                    List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

                    if (positionOnBoard == null)
                    {
                        return 0;
                    }

                    for (int i = 0; i < positionOnBoard.Count; i++)
                    {
                        if (positionOnBoard[i].SecurityNameCode == _tradeAssetInPortfolio.ValueString)
                        {
                            portfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                            break;
                        }
                    }
                }

                if (portfolioPrimeAsset == 0)
                {
                    SendNewLogMessage("Can`t found portfolio " + _tradeAssetInPortfolio.ValueString, OsEngine.Logging.LogMessageType.Error);
                    return 0;
                }

                decimal stopPct = _stopPercent.ValueDecimal / 100m;
                if (stopPct <= 0)
                    return 0;

                // ======== ДОБАВЛЯЕМ НОВЫЕ ПАРАМЕТРЫ ========
                decimal slippagePct = _slippagePercent.ValueDecimal / 100m;
                decimal feePct = _feePercent.ValueDecimal / 100m;

                // ======== РЕАЛЬНЫЙ ПРОЦЕНТ РИСКА ========
                //     стоп     проскальз.    комиссия вход+выход
                decimal realStopPct = stopPct + slippagePct + feePct * 2m;

                // Перестраховка
                if (realStopPct <= 0)
                    realStopPct = stopPct;


                decimal riskMoney = portfolioPrimeAsset * (_volume.ValueDecimal / 100m);
                decimal positionMoney = riskMoney / realStopPct;

                decimal qty = positionMoney / tab.PriceBestAsk / tab.Security.Lot;

                if (tab.StartProgram == StartProgram.IsOsTrader)
                {
                    if (tab.Security.UsePriceStepCostToCalculateVolume == true
                      && tab.Security.PriceStep != tab.Security.PriceStepCost
                      && tab.PriceBestAsk != 0
                      && tab.Security.PriceStep != 0
                      && tab.Security.PriceStepCost != 0)
                    {// расчёт количества контрактов для фьючерсов и опционов на Мосбирже
                        qty = positionMoney / (tab.PriceBestAsk / tab.Security.PriceStep * tab.Security.PriceStepCost);
                    }
                    qty = Math.Round(qty, tab.Security.DecimalsVolume);
                }
                else
                {
                    qty = Math.Round(qty, 7);
                }

                return qty;
            }

            return volume;
        }
             
             
             */



        }


    }
}