using System;
using System.Data;

public partial class DashboardPage : System.Web.UI.Page
{

    protected void Page_Load(object sender, EventArgs e)
    {
        if (IsPostBack) return;
        DataSet ds = LegacyRepository.Load();
        int blocked = ds.Tables["Customers"].Select("Blocked=true").Length;
        int review = ds.Tables["Orders"].Select("Status='REVIEW'").Length;
        decimal debt = 0m;
        int i;
        for (i = 0; i < ds.Tables["Customers"].Rows.Count; i++) debt += Convert.ToDecimal(ds.Tables["Customers"].Rows[i]["OutstandingDebt"]);
        Kpis.Text = "<div class='kpi'><b>" + ds.Tables["Customers"].Rows.Count + "</b>customers</div>" +
                    "<div class='kpi'><b>" + blocked + "</b>blocked</div>" +
                    "<div class='kpi'><b>" + review + "</b>manual reviews</div>" +
                    "<div class='kpi'><b>" + debt.ToString("0.00") + " €</b>outstanding debt</div>";
        OrdersGrid.DataSource = Tail(ds.Tables["Orders"], 8); OrdersGrid.DataBind();
        AuditGrid.DataSource = Tail(ds.Tables["Audit"], 8); AuditGrid.DataBind();
    }

    private DataTable Tail(DataTable source, int count)
    {
        DataTable result = source.Clone();
        int start = source.Rows.Count - count; if (start < 0) start = 0;
        int i; for (i = source.Rows.Count - 1; i >= start; i--) result.ImportRow(source.Rows[i]);
        return result;
    }
}
