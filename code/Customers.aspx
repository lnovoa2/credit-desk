<%@ Page Title="Customers" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true" CodeFile="Customers.aspx.cs" Inherits="CustomersPage" %>
<asp:Content ID="Body" ContentPlaceHolderID="MainContent" runat="server">
<h2>Customer Credit File</h2>
<div class="box">Manual override is a commercial exception used by credit rules. The action button toggles the Override column; it does not unblock blocked customers or bypass hard stops.</div>
<asp:GridView ID="CustomerGrid" runat="server" CssClass="grid" AutoGenerateColumns="false" OnRowCommand="CustomerGrid_RowCommand">
<Columns>
<asp:BoundField DataField="Id" HeaderText="Id"/><asp:BoundField DataField="Name" HeaderText="Name"/><asp:BoundField DataField="Country" HeaderText="Country"/>
<asp:BoundField DataField="CreditLimit" HeaderText="Credit limit"/><asp:BoundField DataField="OutstandingDebt" HeaderText="Debt"/><asp:BoundField DataField="OverdueDays" HeaderText="Overdue"/>
<asp:BoundField DataField="RiskScore" HeaderText="Risk"/><asp:BoundField DataField="Blocked" HeaderText="Blocked"/><asp:BoundField DataField="ManualOverride" HeaderText="Override"/>
<asp:ButtonField ButtonType="Button" CommandName="ToggleOverride" HeaderText="Action" Text="Toggle manual override" />
</Columns>
</asp:GridView>
<asp:Label ID="Message" runat="server" />
</asp:Content>
