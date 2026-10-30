<%@ Page Title="Night Batch" Language="C#" MasterPageFile="~/Site.master" AutoEventWireup="true" CodeFile="Batch.aspx.cs" Inherits="BatchPage" %>
<asp:Content ID="Body" ContentPlaceHolderID="MainContent" runat="server">
<h2>Night Credit Batch</h2>
<div class="box">Runs automatically every 15 seconds in this local fixture. This page refreshes every 10 seconds; use Run batch now to force an immediate recalculation.</div>
<script type="text/javascript">setTimeout(function(){ window.location.reload(); }, 10000);</script>
<p><b>Last run:</b> <asp:Label ID="LastRun" runat="server" /></p>
<p><b>Executions:</b> <asp:Label ID="Executions" runat="server" /></p>
<p><b>Message:</b> <asp:Label ID="BatchMessage" runat="server" /></p>
<asp:Button ID="Run" runat="server" Text="Run batch now" CssClass="btn" OnClick="Run_Click" />
<h3>Runtime flags</h3><asp:Literal ID="Flags" runat="server" />
</asp:Content>
