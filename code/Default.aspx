<%@ Page Title="Dashboard" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true" CodeFile="Default.aspx.cs" Inherits="DashboardPage" %>
<asp:Content ID="Body" ContentPlaceHolderID="MainContent" runat="server">
<h2>Operations Dashboard</h2>
<div class="box">Internal build <b>2.7.14</b>. Business behavior is defined by application code; printed procedures may be outdated.</div>
<asp:Literal ID="Kpis" runat="server" />
<h3>Latest saved decisions</h3>
<asp:GridView ID="OrdersGrid" runat="server" CssClass="grid" AutoGenerateColumns="true" />
<h3>Latest audit</h3>
<asp:GridView ID="AuditGrid" runat="server" CssClass="grid" AutoGenerateColumns="true" />
</asp:Content>
