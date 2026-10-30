using System;
using System.Data;
using System.Globalization;
using System.Web.UI.WebControls;

public partial class OrderEntryPage : System.Web.UI.Page
{

    protected void Page_Load(object sender, EventArgs e)
    {
        if (!IsPostBack) {
        DataSet ds = LegacyRepository.Load();
        int i;
        for (i = 0; i < ds.Tables["Customers"].Rows.Count; i++)
        {
            DataRow c = ds.Tables["Customers"].Rows[i];
            CustomerId.Items.Add(new ListItem(Convert.ToString(c["Id"]) + " - " + Convert.ToString(c["Name"]), Convert.ToString(c["Id"])));
        }
        }
        BindRecentOrders();
    }

    protected void Submit_Click(object sender, EventArgs e)
    {
        decimal amount;
        string normalized = (Amount.Text ?? "").Trim().Replace(',', '.');
        if (!Decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount <= 0m)
        {
            Result.Text = "<div class='result bad'>Invalid amount.</div>";
            return;
        }

        DataSet ds = LegacyRepository.Load();
        DataRow customer = LegacyRepository.FindCustomer(ds, CustomerId.SelectedValue);
        if (customer == null)
        {
            Result.Text = "<div class='result bad'>Customer not found.</div>";
            return;
        }

        // Deliberadamente en code-behind: validación comercial histórica fuera del servicio.
        if (amount < 25m && Payment.SelectedValue == "INVOICE")
        {
            Result.Text = "<div class='result warn'>Orders below 25 EUR cannot be invoiced.</div>";
            return;
        }

        CreditDecision decision = CreditDecisionService.Evaluate(ds, customer, amount, Payment.SelectedValue, Priority.SelectedValue);
        DataRow order = ds.Tables["Orders"].NewRow();
        string orderId = "ORD-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
        order["Id"] = orderId;
        order["CustomerId"] = CustomerId.SelectedValue;
        order["Amount"] = amount;
        order["PaymentMethod"] = Payment.SelectedValue;
        order["Priority"] = Priority.SelectedValue;
        order["Status"] = decision.Status;
        order["DecisionCode"] = decision.Code;
        order["DecisionText"] = decision.Explanation;
        order["CreatedAt"] = DateTime.Now;
        ds.Tables["Orders"].Rows.Add(order);
        LegacyRepository.AddAudit(ds, "ORDER", orderId, decision.Status + "/" + decision.Code + "; risk=" + decision.EffectiveRisk + "; exposure=" + decision.ExposureAfterOrder);
        LegacyRepository.Save(ds);

        string css = decision.Status == "APPROVED" ? "ok" : (decision.Status == "BLOCKED" ? "bad" : "warn");
        Result.Text = "<div class='result " + css + "'><b>" + decision.Status + " / " + decision.Code + "</b><br/>" +
                      decision.Explanation + "<br/>Effective risk: " + decision.EffectiveRisk +
                      "<br/>Exposure after order: " + decision.ExposureAfterOrder.ToString("0.00") + " €</div>";
        BindRecentOrders();
    }
    private void BindRecentOrders()
    {
        DataSet ds = LegacyRepository.Load();
        RecentOrdersGrid.DataSource = Tail(ds.Tables["Orders"], 10);
        RecentOrdersGrid.DataBind();
    }

    private DataTable Tail(DataTable source, int count)
    {
        DataTable result = source.Clone();
        int start = source.Rows.Count - count; if (start < 0) start = 0;
        int i; for (i = source.Rows.Count - 1; i >= start; i--) result.ImportRow(source.Rows[i]);
        return result;
    }
}
