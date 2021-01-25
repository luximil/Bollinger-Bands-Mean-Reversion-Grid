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

        [Parameter("Max. Account Risk %", DefaultValue = 20, Group = "Risk Management")]
        public double MaxAccountRiskPct { get; set; }

        [Parameter("Initial SL in Pips", DefaultValue = 10, Group = "Risk Management")]
        public double InitialSLPips { get; set; }


        BollingerBands BB;
        DateTime lastGridEntry;
        //List<Grid> ActiveGrids;
        double UnitTradingCost;
        Dictionary<string, double[]> KellyParameters;

        protected override void OnStart()
        {
            BB = Indicators.BollingerBands(BBSource, BBPeriods, BBStDev, BBMAType);
            //ActiveGrids = new List<Grid>();
            UnitTradingCost = RoundLotCost / 1000000;

            KellyParameters = new Dictionary<string, double[]>();
            KellyParameters.Add("WinningTrades", new double[GridLevelCount]);
            KellyParameters.Add("LosingTrades", new double[GridLevelCount]);
            KellyParameters.Add("Profit", new double[GridLevelCount]);
            KellyParameters.Add("Loss", new double[GridLevelCount]);

            Positions.Closed += OnPositionClosed;
        }

        protected override void OnTick()
        {
            if(lastGridEntry < Bars.OpenTimes.LastValue)
            {
                // Look for grid entry signals.

                if(Bars.ClosePrices.LastValue < BB.Bottom.LastValue && Bars.ClosePrices.Last(1) >= BB.Bottom.Last(1))
                {
                    // Price has crossed the lower Bollinger Band.
                    // Enter long grid.
                    bool gridEntered = CreateGridPositions(TradeType.Buy);
                    
                    if(gridEntered)
                    {
                        lastGridEntry = Bars.OpenTimes.LastValue;
                    }
                }

                else if(Bars.ClosePrices.LastValue > BB.Top.LastValue && Bars.ClosePrices.Last(1) <= BB.Top.Last(1))
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
        }

        protected override void OnStop()
        {
            // Put your deinitialization logic here
        }

        protected bool CreateGridPositions(TradeType direction)
        {
            // Compute take profit for each grid level.
            double[] tpLevels = new double[GridLevelCount];

            for(int i=0;i<tpLevels.Length;i++)
            {
                if(direction == TradeType.Buy)
                {
                    tpLevels[i] = BB.Main.LastValue - i * (BB.Main.LastValue - BB.Bottom.LastValue) / GridLevelCount;
                }

                else if(direction == TradeType.Sell)
                {
                    tpLevels[i] = BB.Main.LastValue + i * (BB.Top.LastValue - BB.Main.LastValue) / GridLevelCount;
                }
            }

            Position[] gridPositions = new Position[GridLevelCount];
            bool errorEnteringTrade = false;

            for(int i=0; i<GridLevelCount;i++)
            {
                double currentUnitCostPips = (UnitTradingCost + 2*Symbol.Spread / Symbol.PipSize);
                double TPPips = Math.Abs(Bars.ClosePrices.LastValue - tpLevels[i]) / Symbol.PipSize;

                // If the cost of trading level i position is greater than the target return, don't enter this level's or the following level's positions.
                if(currentUnitCostPips >= TPPips)
                {
                    Print(string.Format("New grid entered with only {0} levels, due to cost/target return inefficiency.", i));
                    break;
                }

                string positionLabel = string.Format("{0} - Level {1}", RobotId, i + 1);

                TradeResult result = ExecuteMarketOrder(direction, Symbol.Name, ComputeKellyVolume(i), positionLabel, InitialSLPips, TPPips);

                if(result.IsSuccessful)
                {
                    gridPositions[i] = result.Position;
                }

                else
                {
                    //gridPositions[i] = null;
                    Print(string.Format("Grid level {0} {1} position could not be entered. Error: {2}.", i + 1, direction.ToString(), result.Error));
                    errorEnteringTrade = true;
                }
            }

            //ActiveGrids.Add(new Grid(gridPositions));

            return !errorEnteringTrade;
        }

        protected double ComputeKellyVolume(int level)
        {
            double risk = InitialAccountRiskPct / 100;

            double WinningTrades = KellyParameters["WinningTrades"][level];
            double LosingTrades = KellyParameters["LosingTrades"][level];
            double Profit = KellyParameters["Profit"][level];
            double Loss = KellyParameters["Loss"][level];

            if (LosingTrades != 0 && Loss != 0 && Profit != 0)
            {
                double W = WinningTrades / LosingTrades;
                double R = Profit / Math.Abs(Loss);

                InitialAccountRiskPct = Math.Min((W - (1 - W) / R), MaxAccountRiskPct / 100);
            }

            return Symbol.NormalizeVolumeInUnits(Account.FreeMargin * risk);
        }

        protected void OnPositionClosed(PositionClosedEventArgs args)
        {
            if(!args.Position.Label.StartsWith(RobotId))
            {
                // The closed position was not opened by this bot.
                // Skip position.
                return;
            }

            int level = int.Parse(args.Position.Label.Last().ToString()) - 1;

            double netProfit = args.Position.NetProfit;

            if(netProfit > 0)
            {
                KellyParameters["WinningTrades"][level] += 1;
                KellyParameters["Profit"][level] += netProfit;
            }

            else
            {
                KellyParameters["LosingTrades"][level] += 1;
                KellyParameters["Loss"][level] += netProfit;
            }

            return;
        }
    }

    /*
    public class Grid
    {
        Position[] GridPositions;

        public Grid(Position[] positions)
        {
            GridPositions = positions;
        }
    }
    */
}
