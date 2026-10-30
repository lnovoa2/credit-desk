<%@ Application Language="C#" %>
<script runat="server">
void Application_Start(object sender, EventArgs e)
{
    LegacyRepository.EnsureDatabase();
    NightlyBatch.Start();
}

void Application_End(object sender, EventArgs e)
{
    NightlyBatch.Stop();
}
</script>
