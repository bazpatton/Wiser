namespace Wiser.Monitor.Services;

/// <summary>
/// Prepayment display readings move in coarser steps than receipt Vol Credit (e.g. +86 on meter vs 86000 credited).
/// Cost uses: credit consumed (raw) × this factor × (Amount ÷ Vol Credit).
/// </summary>
public static class GasCreditMeterPricing
{
    public const decimal RawConsumptionToVolCreditUnits = 100m;
}
