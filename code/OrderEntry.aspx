<%@ Page Title="New Order" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true" CodeFile="OrderEntry.aspx.cs" Inherits="OrderEntryPage" %>
<asp:Content ID="Body" ContentPlaceHolderID="MainContent" runat="server">
<h2>Order Credit Authorization</h2>
<div class="field"><label>Customer</label><asp:DropDownList ID="CustomerId" runat="server" /></div>
<div class="field"><label>Amount (€)</label><asp:TextBox ID="Amount" runat="server" Text="250" /></div>
<div class="field"><label>Payment method</label><asp:DropDownList ID="Payment" runat="server"><asp:ListItem>INVOICE</asp:ListItem><asp:ListItem>TRANSFER</asp:ListItem><asp:ListItem>CARD</asp:ListItem></asp:DropDownList></div>
<div class="field"><label>Priority</label><asp:DropDownList ID="Priority" runat="server"><asp:ListItem>NORMAL</asp:ListItem><asp:ListItem>URGENT</asp:ListItem></asp:DropDownList></div>
<asp:Button ID="Submit" runat="server" Text="Evaluate and save" CssClass="btn" OnClick="Submit_Click" />
<asp:Literal ID="Result" runat="server" />
<h3>Recent saved orders</h3>
<asp:GridView ID="RecentOrdersGrid" runat="server" CssClass="grid" AutoGenerateColumns="true" />
<p class="small">The displayed reason is not necessarily the only rule evaluated. Order and risk rules are sequential and short-circuit.</p>
</asp:Content>
