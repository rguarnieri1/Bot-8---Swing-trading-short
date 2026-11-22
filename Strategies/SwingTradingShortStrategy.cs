using BotCripto.Models;

namespace BotCripto.Strategies;

// Swing trading short-only su candele chiuse (timeframe configurato, tipicamente 1d):
//  1) trend di fondo ribassista: Close < EMA50 e EMA20 < EMA50
//  2) rimbalzo: nelle ultime candele l'RSI14 è salito sopra RsiPullback e il massimo ha toccato l'EMA20
//  3) ripartenza al ribasso: candela ribassista che chiude sotto l'EMA20 e sotto il minimo della candela precedente,
//     RSI in calo e non ipervenduto
// Stop loss sopra il massimo dello swing recente (più un buffer ATR); il target lo calcola il RiskManager (R:R configurato).
public class SwingTradingShortStrategy
{
    private const int FastEma = 20;
    private const int SlowEma = 50;
    private const int AtrPeriod = 14;
    public const int MinCandles = SlowEma + 20;

    // Parametri regolabili (default = comportamento originale); si impostano da riga di comando nel backtest con --p chiave=valore
    private static int PullbackLookback = 8;       // candele entro cui deve essere avvenuto il rimbalzo
    private static int SwingLookback = 10;         // candele per il massimo dello swing (stop)
    private static decimal AtrStopBuffer = 0.5m;   // buffer sopra lo swing, in ATR
    private static decimal RsiPullback = 45m;      // l'RSI deve essere salito sopra questa soglia durante il rimbalzo
    private static decimal RsiMin = 30m;
    private static decimal MaxStopPercent = 0.15m; // stop più largo = setup scartato
    private static decimal MinStopPercent = 0m;    // stop più stretto = setup scartato
    private static decimal PullbackEmaTol = 1.05m; // il massimo del rimbalzo non deve superare EMA50 * questo valore
    private static decimal VolMult = 0m;           // 0 = off; volume candela segnale >= VolMult * media 20 candele
    private static decimal Ema50Falling = 0m;      // 1 = richiede EMA50 in discesa rispetto a 5 candele prima
    private static decimal MinTrendGap = 0m;       // distanza minima (%) tra EMA20 ed EMA50 rispetto a EMA50
    private static decimal MaxRsiNow = 100m;       // RSI massimo alla candela segnale

    public static void SetParam(string key, decimal v)
    {
        switch (key.ToLowerInvariant())
        {
            case "pullbacklookback": PullbackLookback = (int)v; break;
            case "swinglookback": SwingLookback = (int)v; break;
            case "atrstopbuffer": AtrStopBuffer = v; break;
            case "rsipullback": RsiPullback = v; break;
            case "rsimin": RsiMin = v; break;
            case "maxstop": MaxStopPercent = v; break;
            case "minstop": MinStopPercent = v; break;
            case "pullbackematol": PullbackEmaTol = v; break;
            case "volmult": VolMult = v; break;
            case "ema50falling": Ema50Falling = v; break;
            case "mintrendgap": MinTrendGap = v; break;
            case "maxrsinow": MaxRsiNow = v; break;
            default: throw new ArgumentException($"Parametro strategia sconosciuto: {key}");
        }
    }

    public AnalysisResult Analyze(string symbol, List<Candle> candles)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "Swing Trading Short",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (ema20.Count < PullbackLookback + 1 || ema50.Count < PullbackLookback + 1 || rsi.Count < PullbackLookback + 2)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        var prev = candles[candles.Count - 2];
        result.CurrentPrice = last.Close;

        // Le serie sono allineate alla fine: l'indice i-esimo dalla fine corrisponde alla candela i-esima dalla fine
        decimal E20(int back) => ema20[ema20.Count - 1 - back];
        decimal E50(int back) => ema50[ema50.Count - 1 - back];
        decimal Rsi(int back) => rsi[rsi.Count - 1 - back];

        var e20 = E20(0);
        var e50 = E50(0);
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;

        // 1) Trend di fondo ribassista
        if (!(last.Close < e50 && e20 < e50)
            || (e50 - e20) / e50 * 100m < MinTrendGap
            || (Ema50Falling > 0 && ema50.Count > 5 && e50 >= E50(5)))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Rimbalzo recente: massimo sull'EMA20 (ma sotto l'EMA50) e RSI salito sopra soglia
        bool pullback = false;
        for (int back = 1; back <= PullbackLookback; back++)
        {
            var c = candles[candles.Count - 1 - back];
            if (c.High >= E20(back) && c.High <= E50(back) * PullbackEmaTol && Rsi(back) > RsiPullback)
            {
                pullback = true;
                break;
            }
        }
        if (!pullback)
        {
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // 3) Ripartenza: candela ribassista sotto EMA20 e sotto il minimo precedente, RSI in calo ma non ipervenduto
        var rsiNow = Rsi(0);
        result.Indicators["RSI"] = rsiNow;
        bool reversal = last.Close < last.Open && last.Close < e20 && last.Close < prev.Low;
        if (!reversal || rsiNow < RsiMin || rsiNow > MaxRsiNow || rsiNow >= Rsi(1))
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }
        if (VolMult > 0 && candles.Count > 21)
        {
            var avgVol = candles.Skip(candles.Count - 21).Take(20).Average(c => c.Volume);
            if (avgVol > 0 && last.Volume < avgVol * VolMult)
            {
                result.Rejection = RejectionReason.RsiExtreme;
                return result;
            }
        }

        // Stop sopra lo swing high recente, con buffer ATR
        var atr = CalculateAtr(candles, AtrPeriod);
        var swingHigh = candles.Skip(candles.Count - SwingLookback).Max(c => c.High);
        var stop = swingHigh + atr * AtrStopBuffer;
        if (stop <= last.Close || (stop - last.Close) / last.Close > MaxStopPercent || (stop - last.Close) / last.Close < MinStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = "SELL";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = trend più marcato (distanza EMA50 da EMA20) e ripartenza più decisa (RSI basso)
        result.Indicators["SignalStrength"] = (e50 - e20) / e50 * 100m + (100m - rsiNow) / 10m;
        return result;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
