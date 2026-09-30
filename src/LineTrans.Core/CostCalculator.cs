namespace LineTrans.Core;

/// <summary>
/// 费用估算（移植自安卓端 <c>util/CostCalculator.kt</c>）。
/// 费用以“每百万 token”的价格配置，乘以当前高峰倍率。
/// </summary>
public static class CostCalculator
{
    public static double CostFor(ModelConfig model, int promptTokens, int completionTokens)
    {
        var b = model.Billing;
        double input = promptTokens / 1_000_000.0 * b.InputPrice;
        double output = completionTokens / 1_000_000.0 * b.OutputPrice;
        return (input + output) * b.CurrentMultiplier;
    }
}
