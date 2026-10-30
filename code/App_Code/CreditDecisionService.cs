using System;
using System.Collections;
using System.Configuration;
using System.Data;

public class CreditDecision
{
    public string Status;
    public string Code;
    public string Explanation;
    public int EffectiveRisk;
    public decimal ExposureAfterOrder;
}

public static class CreditDecisionService
{
    public static CreditDecision Evaluate(DataSet ds, DataRow customer, decimal amount, string paymentMethod, string priority)
    {
        CreditDecision d = new CreditDecision();
        int risk = Convert.ToInt32(customer["RiskScore"]);
        decimal debt = Convert.ToDecimal(customer["OutstandingDebt"]);
        decimal limit = Convert.ToDecimal(customer["CreditLimit"]);
        int overdue = Convert.ToInt32(customer["OverdueDays"]);
        bool blocked = Convert.ToBoolean(customer["Blocked"]);
        bool manualOverride = Convert.ToBoolean(customer["ManualOverride"]);
        string country = Convert.ToString(customer["Country"]);
        DateTime since = Convert.ToDateTime(customer["Since"]);
        string customerId = Convert.ToString(customer["Id"]);
        decimal openOrders = LegacyRepository.SumOpenOrders(ds, customerId);
        int recentOrders = LegacyRepository.CountRecentOrders(ds, customerId, 14);

        // Regla antigua: se penaliza país, recurrencia, deuda y clientes nuevos.
        if (IsSurchargeCountry(country)) risk += 7;
        if (recentOrders >= 3) risk += 8;
        if (debt > limit * 0.65m) risk += 12;
        if (DateTime.Now.Subtract(since).TotalDays < 730) risk += 10;
        if (paymentMethod == "CARD") risk -= 3;
        if (paymentMethod == "INVOICE") risk += 4;
        if (priority == "URGENT") risk += 5;
        if (amount > 3000m) risk += 5;
        if (amount > 10000m) risk += 12;
        if (risk < 0) risk = 0;
        if (risk > 100) risk = 100;

        decimal exposure = debt + openOrders + amount;
        d.EffectiveRisk = risk;
        d.ExposureAfterOrder = exposure;

        int hardOverdue = ConfigInt("HardBlockOverdueDays", 90);
        int highRisk = ConfigInt("HighRiskScore", 72);
        decimal manualReviewAmount = ConfigDecimal("ManualReviewAmount", 7500m);

        // El override NO ignora deuda vencida extrema. Esta excepción histórica suele perderse en migraciones.
        if (blocked && !manualOverride)
            return Fill(d, "BLOCKED", "B01", "Customer marked as blocked");
        if (overdue >= hardOverdue)
            return Fill(d, "BLOCKED", "B90", "Hard overdue threshold exceeded");
        if (exposure > limit * 1.35m && !manualOverride)
            return Fill(d, "BLOCKED", "B35", "Exposure exceeds 135% of credit limit");

        // Casos que requieren revisión humana.
        if (risk >= highRisk && !manualOverride)
            return Fill(d, "REVIEW", "R72", "Effective risk is above review threshold");
        if (exposure > limit && paymentMethod == "INVOICE" && !manualOverride)
            return Fill(d, "REVIEW", "R11", "Invoice pushes exposure over credit limit");
        if (amount >= manualReviewAmount && paymentMethod != "CARD")
            return Fill(d, "REVIEW", "R75", "Large non-card order requires manual review");
        if (priority == "URGENT" && overdue > 20)
            return Fill(d, "REVIEW", "R20", "Urgent order with overdue balance");

        // Regla comercial oculta: clientes con mucha antigüedad conservan margen extra.
        int years = (int)(DateTime.Now.Subtract(since).TotalDays / 365.25);
        if (years >= 15 && exposure <= limit * 1.10m && risk < 65)
            return Fill(d, "APPROVED", "A15", "Long-term customer tolerance applied");

        if (exposure <= limit && risk < highRisk)
            return Fill(d, "APPROVED", "A00", "Within credit and risk limits");

        if (manualOverride && exposure <= limit * 1.50m)
            return Fill(d, "APPROVED", "AOV", "Manual commercial override applied");

        return Fill(d, "REVIEW", "R99", "No automatic rule produced a safe approval");
    }

    private static CreditDecision Fill(CreditDecision d, string status, string code, string text)
    {
        d.Status = status; d.Code = code; d.Explanation = text;
        return d;
    }

    private static bool IsSurchargeCountry(string country)
    {
        string raw = ConfigurationManager.AppSettings["LegacyCountrySurcharge"] ?? "";
        string[] values = raw.Split(',');
        int i;
        for (i = 0; i < values.Length; i++)
            if (values[i].Trim().ToUpperInvariant() == (country ?? "").ToUpperInvariant()) return true;
        return false;
    }

    private static int ConfigInt(string key, int fallback)
    {
        int value;
        return Int32.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback;
    }

    private static decimal ConfigDecimal(string key, decimal fallback)
    {
        decimal value;
        return Decimal.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback;
    }
}
