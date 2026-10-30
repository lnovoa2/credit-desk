using System;
using System.Configuration;
using System.Data;
using System.Threading;

public static class NightlyBatch
{
    private static Timer _timer;
    private static int _running;

    public static void Start()
    {
        if (_timer != null) return;
        int seconds = 45;
        Int32.TryParse(ConfigurationManager.AppSettings["BatchIntervalSeconds"], out seconds);
        if (seconds < 10) seconds = 10;
        _timer = new Timer(new TimerCallback(Tick), null, seconds * 1000, seconds * 1000);
    }

    public static void Stop()
    {
        if (_timer != null) _timer.Dispose();
        _timer = null;
    }

    public static string RunNow()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return "Batch already running";
        try
        {
            DataSet ds = LegacyRepository.Load();
            DataTable t = ds.Tables["Customers"];
            int changed = 0;
            int i;
            for (i = 0; i < t.Rows.Count; i++)
            {
                DataRow c = t.Rows[i];
                int overdue = Convert.ToInt32(c["OverdueDays"]);
                decimal debt = Convert.ToDecimal(c["OutstandingDebt"]);
                decimal limit = Convert.ToDecimal(c["CreditLimit"]);
                int risk = Convert.ToInt32(c["RiskScore"]);
                bool blocked = Convert.ToBoolean(c["Blocked"]);

                int newRisk = risk;
                if (overdue > 60) newRisk += 9;
                else if (overdue > 30) newRisk += 4;
                else if (overdue == 0 && debt < limit * 0.25m) newRisk -= 2;

                if (debt > limit) newRisk += 7;
                if (newRisk > 100) newRisk = 100;
                if (newRisk < 0) newRisk = 0;
                if (newRisk != risk) { c["RiskScore"] = newRisk; changed++; }

                // El batch puede bloquear automáticamente, pero jamás desbloquea.
                if (!blocked && overdue >= 90)
                {
                    c["Blocked"] = true;
                    LegacyState.SetFlag(Convert.ToString(c["Id"]), "AUTO_BLOCKED");
                    changed++;
                }
            }

            LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", "Processed=" + t.Rows.Count + "; Changed=" + changed);
            LegacyRepository.Save(ds);
            LegacyState.LastBatchRun = DateTime.Now;
            LegacyState.BatchExecutions++;
            LegacyState.LastBatchMessage = "Processed " + t.Rows.Count + " customers; changed " + changed;
            return LegacyState.LastBatchMessage;
        }
        catch (Exception ex)
        {
            LegacyState.LastBatchMessage = "ERROR: " + ex.Message;
            return LegacyState.LastBatchMessage;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static void Tick(object state)
    {
        RunNow();
    }
}
