using System;
using System.Collections;

public static class LegacyState
{
    public static readonly object SyncRoot = new object();
    public static Hashtable CustomerFlags = new Hashtable();
    public static DateTime LastBatchRun = DateTime.MinValue;
    public static string LastBatchMessage = "Never executed";
    public static int BatchExecutions = 0;

    public static void SetFlag(string customerId, string value)
    {
        lock (SyncRoot)
        {
            CustomerFlags[customerId] = value;
        }
    }

    public static string GetFlag(string customerId)
    {
        lock (SyncRoot)
        {
            object value = CustomerFlags[customerId];
            return value == null ? "" : value.ToString();
        }
    }
}
