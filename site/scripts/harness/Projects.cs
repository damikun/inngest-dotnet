// What Aspire generates for each project that an AppHost references, which the snippets of the Aspire guide use.
namespace Projects
{
    public sealed class Shop : Aspire.Hosting.IProjectMetadata
    {
        public string ProjectPath => "Shop/Shop.csproj";
    }

    public sealed class Billing : Aspire.Hosting.IProjectMetadata
    {
        public string ProjectPath => "Billing/Billing.csproj";
    }

    public sealed class Worker : Aspire.Hosting.IProjectMetadata
    {
        public string ProjectPath => "Worker/Worker.csproj";
    }
}
