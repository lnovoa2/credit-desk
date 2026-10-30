using System;
using System.Collections;
using System.Text;
using System.Web.UI.WebControls;

public partial class BatchPage : System.Web.UI.Page
{

    protected void Page_Load(object sender, EventArgs e)
    {
        if (!IsPostBack) Refresh();
    }

    protected void Run_Click(object sender, EventArgs e)
    {
        NightlyBatch.RunNow();
        Refresh();
    }

    private void Refresh()
    {
        LastRun.Text = LegacyState.LastBatchRun == DateTime.MinValue ? "Never" : LegacyState.LastBatchRun.ToString("s");
        Executions.Text = LegacyState.BatchExecutions.ToString();
        BatchMessage.Text = LegacyState.LastBatchMessage;
        StringBuilder html = new StringBuilder();
        html.Append("<table class='grid'><tr><th>Customer</th><th>Flag</th></tr>");
        bool hasFlags = false;
        lock (LegacyState.SyncRoot)
        {
            foreach (DictionaryEntry e in LegacyState.CustomerFlags)
            {
                hasFlags = true;
                html.Append("<tr><td>" + Server.HtmlEncode(Convert.ToString(e.Key)) + "</td><td>" + Server.HtmlEncode(Convert.ToString(e.Value)) + "</td></tr>");
            }
            if (!hasFlags) html.Append("<tr><td colspan='2'>No runtime flags yet</td></tr>");
        }
        html.Append("</table>");
        Flags.Text = html.ToString();
    }
}
