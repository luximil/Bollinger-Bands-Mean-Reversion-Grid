using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Indicators;
using System.Collections.Generic;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class BollingerBandsMeanReversionGrid : Robot
    {
        [Parameter("Robot Id", DefaultValue = "Bollinger Bands Mean Reversion Grid")]
        public string RobotId { get; set; }


        [Parameter("Source", Group = "Bollinger Bands")]
        public DataSeries BBSource { get; set; }

        [Parameter("Periods", DefaultValue = 20, Group = "Bollinger Bands")]
        public int BBPeriods { get; set; }

        [Parameter("Standard Deviations", DefaultValue = 2, Group = "Bollinger Bands")]
        public double BBStDev { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.Simple, Group = "Bollinger Bands")]
        public MovingAverageType BBMAType { get; set; }

        [Parameter("Shift", DefaultValue = 0, Group = "Bollinger Bands")]
        public int BBShift { get; set; }


        [Parameter("Level Count", DefaultValue = 3, Group = "Grid")]
        public int GridLevelCount { get; set; }

        [Parameter("Round Lot Commission per Million", DefaultValue = 30, Group = "Risk Management")]
        public double RoundLotCost { get; set; }

        [Parameter("Initial Account Risk %", DefaultValue = 5, Group = "Risk Management")]
        public double InitialAccountRiskPct { get; set; }

        [Parameter("Max. Account Risk % per Grid", DefaultValue = 20, Group = "Risk Management")]
        public double MaxAccountRiskPct { get; set; }

        [Parameter("Initial SL in Pips", DefaultValue = 10, Group = "Risk Management")]
        public double InitialSLPips { get; set; }

        [Parameter("Min. Net Initial TP in Pips", DefaultValue = 20, Group = "Risk Management")]
        public double MinNetInitialTPPips { get; set; }

        [Parameter("Enter Positions after Confirmation", DefaultValue = false, Group = "Risk Management")]
        public bool EntryAfterConfirmation { get; set; }

        [Parameter("Activate Dynamic SL", DefaultValue = true, Group = "Risk Management")]
        public bool DynamicSLActivated { get; set; }

        [Parameter("Activate Dynamic TP", DefaultValue = true, Group = "Risk Management")]
        public bool DynamicTPActivated { get; set; }


        BollingerBands BB;
        DateTime lastGridEntry;
        List<Grid> ActiveGrids;
        double UnitTradingCost;
        Dictionary<string, double[]> KellyParameters;
        int firstIndex;

        protected override void OnStart()
        {
            BB = Indicators.BollingerBands(BBSource, BBPeriods, BBStDev, BBMAType);
            ActiveGrids = new List<Grid>();
            UnitTradingCost = RoundLotCost / 1000000;

            KellyParameters = new Dictionary<string, double[]>();
            KellyParameters.Add("WinningTrades", new double[GridLevelCount]);
            KellyParameters.Add("LosingTrades", new double[GridLevelCount]);
            KellyParameters.Add("Profit", new double[GridLevelCount]);
            KellyParameters.Add("Loss", new double[GridLevelCount]);

            firstIndex = Convert.ToInt32(EntryAfterConfirmation);

            Positions.Closed += OnPositionClosed;
        }

        protected override void OnBar()
        {
            // Garbage collector for ActiveGrids object, in case removal failed on close of its last position.
            var closedGrids = ActiveGrids.Where(grid => grid.GridPositions.All(pos => pos == null));
            foreach (Grid closedGrid in closedGrids)
            {
                ActiveGrids.Remove(closedGrid);
                Print(string.Format("Grid ({0}) closed by garbage collector.", closedGrid.Id));
            }

            //ActiveGrids.RemoveAll(grid => grid.GridPositions.All(pos => pos == null));
        }

        protected override void OnTick()
        {
            if (lastGridEntry < Bars.OpenTimes.LastValue)
            {
                // Look for grid entry signals.

                if (Bars.ClosePrices.Last(firstIndex) < BB.Bottom.Last(BBShift + firstIndex) && Bars.ClosePrices.Last(1 + firstIndex) >= BB.Bottom.Last(BBShift + 1 + firstIndex))
                {
                    // Price has crossed the lower Bollinger Band.
                    // Enter long grid.
                    bool gridEntered = CreateGridPositions(TradeType.Buy);

                    if (gridEntered)
                    {
                        lastGridEntry = Bars.OpenTimes.LastValue;
                    }
                }

                else if (Bars.ClosePrices.Last(firstIndex) > BB.Top.Last(BBShift + firstIndex) && Bars.ClosePrices.Last(1 + firstIndex) <= BB.Top.Last(BBShift + 1 + firstIndex))
                {
                    // Price has crossed the upper Bollinger Band.
                    // Enter short grid.

                    bool gridEntered = CreateGridPositions(TradeType.Sell);

                    if (gridEntered)
                    {
                        lastGridEntry = Bars.OpenTimes.LastValue;
                    }
                }
            }

            if (ActiveGrids.Count > 0)
            {
                if (DynamicTPActivated)
                {
                    int buyClosingIndex = -1;
                    int sellClosingIndex = -1;

                    for (int i = 0; i < GridLevelCount; i++)
                    {
                        // If index < 0, then no dynamic TP level yet found.
                        // This check is needed, so that the index is not set lower on the following loop step, once it is set.
                        if (buyClosingIndex < 0)
                        {
                            if (Bars.ClosePrices.LastValue > BB.Main.Last(BBShift) - i * (BB.Main.Last(BBShift) - BB.Bottom.Last(BBShift)) / GridLevelCount)
                            {
                                buyClosingIndex = i;
                            }
                        }

                        // If index < 0, then no dynamic TP level yet found.
                        // This check is needed, so that the index is not set lower on the following loop step, once it is set.
                        if (sellClosingIndex < 0)
                        {
                            if (Bars.ClosePrices.LastValue < BB.Main.Last(BBShift) + i * (BB.Top.Last(BBShift) - BB.Main.Last(BBShift)) / GridLevelCount)
                            {
                                sellClosingIndex = i;
                            }
                        }
                    }

                    foreach (Grid grid in ActiveGrids)
                    {
                        if (buyClosingIndex >= 0 && grid.Type == TradeType.Buy)
                        {
                            for (int i = buyClosingIndex; i < GridLevelCount; i++)
                            {
                                if (grid.GridPositions[i] != null)
                                {
                                    TradeResult result = grid.GridPositions[i].Close();

                                    if (result.IsSuccessful)
                                    {
                                        Print(string.Format("Grid ({0}): Position level {1} closed on dynamic TP at {2}. Original TP: {3}.", grid.Id, i + 1, Bars.ClosePrices.LastValue, grid.GridPositions[i].TakeProfit));
                                    }

                                    else
                                    {
                                        Print(string.Format("Grid ({0}): Position level {1} could not be closed on dynamic TP at {2}. Original TP: {3}. Error: {4}.", grid.Id, i + 1, Bars.ClosePrices.LastValue, grid.GridPositions[i].TakeProfit, result.Error));
                                    }
                                }
                            }
                        }

                        else if (sellClosingIndex >= 0 && grid.Type == TradeType.Sell)
                        {
                            for (int i = sellClosingIndex; i < GridLevelCount; i++)
                            {
                                if (grid.GridPositions[i] != null)
                                {
                                    TradeResult result = grid.GridPositions[i].Close();

                                    if (result.IsSuccessful)
                                    {
                                        Print(string.Format("Grid ({0}): Position level {1} closed on dynamic TP at {2}. Original TP: {3}.", grid.Id, i + 1, Bars.ClosePrices.LastValue, grid.GridPositions[i].TakeProfit));
                                    }

                                    else
                                    {
                                        Print(string.Format("Grid ({0}): Position level {1} could not be closed on dynamic TP at {2}. Original TP: {3}. Error: {4}.", grid.Id, i + 1, Bars.ClosePrices.LastValue, grid.GridPositions[i].TakeProfit, result.Error));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        protected override void OnStop()
        {
            Print("Active grids in stack " + ActiveGrids.Count);
            foreach (Grid grid in ActiveGrids)
            {
                string message = "";

                for (int i = 0; i < GridLevelCount; i++)
                {
                    string pos;

                    if (grid.GridPositions[i] == null)
                    {
                        pos = "null";
                    }

                    else
                    {
                        pos = grid.GridPositions[i].ToString();
                    }
                    message += string.Format("P{0}: {1}, ", i, pos);
                }

                Print(message.Substring(0, message.Length - 2));
            }

            Print("Open positions " + Positions.Where(pos => pos.Label.StartsWith(RobotId) && pos.SymbolName == Symbol.Name).ToList().Count);

            for (int i = 0; i < GridLevelCount; i++)
            {
                double WinningTrades = KellyParameters["WinningTrades"][i];
                double LosingTrades = KellyParameters["LosingTrades"][i];
                double Profit = KellyParameters["Profit"][i];
                double Loss = KellyParameters["Loss"][i];

                Print(string.Format("Level {0}. Winning Trades: {1}, Losing Trades: {2}, Profit: {3}, Loss: {4}.", i + 1, WinningTrades, LosingTrades, Profit, Loss));
            }
        }

        protected bool CreateGridPositions(TradeType direction)
        {
            // Compute take profit for each grid level.
            double[] tpLevels = new double[GridLevelCount];

            for (int i = 0; i < tpLevels.Length; i++)
            {
                if (direction == TradeType.Buy)
                {
                    tpLevels[i] = BB.Main.Last(BBShift + firstIndex) - i * (BB.Main.Last(BBShift + firstIndex) - BB.Bottom.Last(BBShift + firstIndex)) / GridLevelCount;
                }

                else if (direction == TradeType.Sell)
                {
                    tpLevels[i] = BB.Main.Last(BBShift + firstIndex) + i * (BB.Top.Last(BBShift + firstIndex) - BB.Main.Last(BBShift + firstIndex)) / GridLevelCount;
                }
            }

            Position[] gridPositions = new Position[GridLevelCount];
            bool errorEnteringTrade = true;

            for (int i = 0; i < GridLevelCount; i++)
            {
                double currentUnitCostPips = (UnitTradingCost + 2 * Symbol.Spread / Symbol.PipSize);
                double TPPips = Math.Round(Math.Abs(Bars.ClosePrices.Last(firstIndex) - tpLevels[i]) / Symbol.PipSize, 1);

                // If the cost of trading level i position is greater than the target return, don't enter this level's or the following level's positions.
                if ((currentUnitCostPips + MinNetInitialTPPips) >= TPPips)
                {
                    if (i > 0)
                    {
                        Print(string.Format("New grid entered with only {0} levels, due to cost/target return inefficiency.", i));
                        break;
                    }

                    else
                    {
                        return errorEnteringTrade;
                    }
                }

                string positionLabel = string.Format("{0} - Level {1}", RobotId, i + 1);

                TradeResult result = ExecuteMarketOrder(direction, Symbol.Name, ComputeKellyVolume(i), positionLabel, InitialSLPips, TPPips);

                if (result.IsSuccessful)
                {
                    gridPositions[i] = result.Position;
                    errorEnteringTrade = false;
                }

                else
                {
                    //gridPositions[i] = null;
                    Print(string.Format("Grid level {0} {1} position could not be entered. Error: {2}.", i + 1, direction.ToString(), result.Error));
                }
            }

            string gridId = "";
            foreach (Position pos in gridPositions)
            {
                if (pos != null)
                {
                    gridId += pos.Id + ".";
                }

                else
                {
                    gridId += "-1.";
                }
            }

            gridId = gridId.Substring(0, gridId.Length - 1);

            ActiveGrids.Add(new Grid(gridId, direction, gridPositions));

            return !errorEnteringTrade;
        }

        protected double ComputeKellyVolume(int level)
        {
            double risk = InitialAccountRiskPct / 100 / GridLevelCount;

            double WinningTrades = KellyParameters["WinningTrades"][level];
            double LosingTrades = KellyParameters["LosingTrades"][level];
            double Profit = KellyParameters["Profit"][level];
            double Loss = KellyParameters["Loss"][level];

            if (LosingTrades != 0 && Loss != 0 && Profit != 0)
            {
                double W = WinningTrades / LosingTrades;
                double R = Profit / Math.Abs(Loss);

                risk = (W - (1 - W) / R);
            }

            double volume = Symbol.VolumeInUnitsMin;

            if (risk > 0)
            {
                volume = Math.Max(volume, Symbol.NormalizeVolumeInUnits(Account.FreeMargin * Math.Min(risk, MaxAccountRiskPct / 100 / GridLevelCount)));
            }

            return volume;
        }

        protected void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (!args.Position.Label.StartsWith(RobotId))
            {
                // The closed position was not opened by this bot.
                // Skip position.
                return;
            }

            int level = int.Parse(args.Position.Label.Last().ToString()) - 1;

            double netProfit = args.Position.NetProfit;

            if (netProfit > 0)
            {
                KellyParameters["WinningTrades"][level] += 1;
                KellyParameters["Profit"][level] += netProfit;
            }

            else
            {
                KellyParameters["LosingTrades"][level] += 1;
                KellyParameters["Loss"][level] += netProfit;
            }

            // Update trailing stop on TP closing.
            //args.Reason == PositionCloseReason.TakeProfit)
            if (DynamicSLActivated && args.Reason != PositionCloseReason.StopLoss)
            {
                // If the position is not the last level position, update trailing stop of the higher level positions.
                if (level > 0)
                {
                    foreach (Grid grid in ActiveGrids)
                    {
                        if (grid.GridPositions[level] == null)
                        {
                            continue;
                        }

                        if (grid.GridPositions[level].Id == args.Position.Id)
                        {
                            // Update stop loss for the other positions.
                            double newSL;

                            if (args.Position.TradeType == TradeType.Buy)
                            {
                                newSL = Math.Round(Math.Max(grid.LastClosedTradeTP, args.Position.EntryPrice + UnitTradingCost + Symbol.Ask - Symbol.Bid), Symbol.Digits);
                            }

                            //if(args.Position.TradeType == TradeType.Sell)
                            else
                            {
                                newSL = Math.Round(Math.Min(grid.LastClosedTradeTP, args.Position.EntryPrice - UnitTradingCost - Symbol.Ask + Symbol.Bid), Symbol.Digits);
                            }

                            int i = level - 1;
                            while (i >= 0)
                            {
                                TradeResult result = grid.GridPositions[i].ModifyStopLossPrice(newSL);

                                if (result.IsSuccessful)
                                {
                                    Print(string.Format("Grid ({0}): SL moved to {1} for position level {2} after position in level {3} closed.", grid.Id, result.Position.StopLoss.Value, i + 1, level));
                                }

                                else
                                {
                                    Print(string.Format("Grid ({0}): SL could not be moved to {1} for position level {2} after position in level {3} closed. Error: {4}.", grid.Id, newSL, i + 1, level, result.Error));
                                }

                                i--;
                            }

                            if (args.Reason == PositionCloseReason.TakeProfit)
                            {
                                grid.LastClosedTradeTP = args.Position.TakeProfit.Value;
                            }

                            else
                            {
                                grid.LastClosedTradeTP = History.FindLast(args.Position.Label, args.Position.SymbolName, args.Position.TradeType).ClosingPrice;
                            }

                            grid.GridPositions[level] = null;

                            break;
                        }
                    }
                }
            }

            // Update ActiveGrids object.
            if (level == 0)
            {
                Grid gridToRemove = ActiveGrids.Find(x => x.GridPositions[level] != null && x.GridPositions[level].Id == args.Position.Id);
                int countBeforeRemove = ActiveGrids.Count;
                string gridToRemoveId = gridToRemove.Id;
                ActiveGrids.Remove(gridToRemove);
                //ActiveGrids.RemoveAll(grid => grid.GridPositions[level] != null && grid.GridPositions[level].Id == args.Position.Id);

                if (countBeforeRemove > ActiveGrids.Count)
                {
                    Print(string.Format("Grid ({0}) removed on close of its last position", gridToRemoveId));
                }
            }

            return;
        }
    }

    public class Grid
    {
        public string Id;
        public TradeType Type;
        public Position[] GridPositions;
        public double LastClosedTradeTP;

        //string id, 
        public Grid(string id, TradeType type, Position[] positions)
        {
            Id = id;
            Type = type;
            GridPositions = positions;

            if (type == TradeType.Buy)
            {
                LastClosedTradeTP = double.NegativeInfinity;
            }

            else if (type == TradeType.Sell)
            {
                LastClosedTradeTP = double.PositiveInfinity;
            }
        }
    }
}
