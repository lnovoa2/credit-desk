using System;
using System.Data;
using System.Web.UI.WebControls;

public partial class CustomersPage : System.Web.UI.Page
{

    protected void Page_Load(object sender, EventArgs e)
    {
        if (!IsPostBack) BindGrid();
    }

    protected void CustomerGrid_RowCommand(object sender, GridViewCommandEventArgs e)
    {
        if (e.CommandName != "ToggleOverride") return;
        int rowIndex = Convert.ToInt32(e.CommandArgument);
        string id = CustomerGrid.Rows[rowIndex].Cells[0].Text;
        DataSet ds = LegacyRepository.Load();
        DataRow customer = LegacyRepository.FindCustomer(ds, id);
        if (customer != null)
        {
            bool next = !Convert.ToBoolean(customer["ManualOverride"]);
            customer["ManualOverride"] = next;
            LegacyRepository.AddAudit(ds, "USER", id, "ManualOverride=" + next);
            LegacyRepository.Save(ds);
            Message.Text = "Override changed for " + id;
        }
        BindGrid();
    }

    private void BindGrid()
    {
        DataSet ds = LegacyRepository.Load();
        CustomerGrid.DataSource = ds.Tables["Customers"];
        CustomerGrid.DataBind();
    }
}
