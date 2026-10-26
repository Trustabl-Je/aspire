// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment resources are experimental.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdApiProvisioningTests(ITestOutputHelper outputHelper)
{
    private const string TenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    private const string ClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";
    private const string ScopeDescription = "Allows the app to call the API on behalf of the signed-in user.";

    [Fact]
    public async Task WithScope_UsesDefaultApplicationIdUriSetBySecondDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithRedirectUris("https://localhost:7123/signin-oidc")
            .WithScope("access_as_user", ScopeDescription);

        Assert.Null(api.Resource.ApplicationIdUri);
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApplicationIdUri_Default_ExposesApiWithoutScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithApplicationIdUri();

        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApplicationIdUri_Custom_IsWrittenToTheApplicationWithoutASecondDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope =>
            {
                scope.DisplayName = "Read orders";
                scope.RequiresAdminConsent = true;
                scope.UserConsentDisplayName = "Read your orders";
                scope.UserConsentDescription = "Allows the app to read your orders.";
            })
            .WithScope("orders.write", "Write orders.", scope => scope.IsEnabled = false);

        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void DefaultProvisioning_DoesNotExposeAnApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddEntraIdApplication("entra-api").Resource.GetBicepTemplateString();

        Assert.DoesNotContain("identifierUris", bicep);
        Assert.DoesNotContain("oauth2PermissionScopes", bicep);
        Assert.DoesNotContain("applicationIdUri", bicep);
        Assert.DoesNotContain("requiredResourceAccess", bicep);
        Assert.DoesNotContain("oauth2PermissionGrants", bicep);
    }

    [Fact]
    public void GetBicepTemplateString_IsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"))
            .WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));

        Assert.Equal(api.Resource.GetBicepTemplateString(), api.Resource.GetBicepTemplateString());
        Assert.Equal(web.Resource.GetBicepTemplateString(), web.Resource.GetBicepTemplateString());
    }

    [Fact]
    public void ScopeIds_AreStableAcrossBuildersAndDistinctPerScope()
    {
        static Guid Resolve(string resourceName, string scopeName)
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            var api = builder.AddEntraIdApplication(resourceName).WithScope(scopeName, ScopeDescription);
            return api.Resource.GetScopeId(Assert.Single(api.Resource.Scopes));
        }

        var id = Resolve("entra-api", "access_as_user");

        Assert.Equal(id, Resolve("entra-api", "access_as_user"));
        Assert.NotEqual(id, Resolve("entra-api", "other"));
        Assert.NotEqual(id, Resolve("other-api", "access_as_user"));

        // The ID is a version 8 GUID in the standard variant, as Graph requires GUID-shaped scope IDs.
        var text = id.ToString("D");
        Assert.Equal('8', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void ScopeIds_AreStableAcrossChangedScopeText()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", "Original description.");
        var second = builder.AddEntraIdApplication("entra-api2").WithScope("access_as_user", "Changed description.");
        var id = first.Resource.GetScopeId(first.Resource.Scopes[0]);

        first.Resource.Scopes[0].Description = "A different description.";

        Assert.Equal(id, first.Resource.GetScopeId(first.Resource.Scopes[0]));
        Assert.NotEqual(id, second.Resource.GetScopeId(second.Resource.Scopes[0]));
    }

    [Fact]
    public void WithScope_UsesExplicitId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription, scope => scope.Id = id);

        Assert.Equal(id, api.Resource.GetScopeId(Assert.Single(api.Resource.Scopes)));
        Assert.Contains("11112222-aaaa-3333-bbbb-4444cccc5555", api.Resource.GetBicepTemplateString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/value")]
    [InlineData("é")]
    public void WithScope_RejectsInvalidNames(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        Assert.ThrowsAny<ArgumentException>(() => api.WithScope(name, ScopeDescription));
        Assert.Empty(api.Resource.Scopes);
    }

    [Fact]
    public void WithScope_RejectsNamesLongerThanGraphAllows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        api.WithScope(new string('a', 120), ScopeDescription);
        Assert.Throws<ArgumentException>(() => api.WithScope(new string('b', 121), ScopeDescription));
    }

    [Fact]
    public void WithScope_RejectsDuplicateNameAndId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555");
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription, scope => scope.Id = id);

        Assert.Throws<ArgumentException>(() => api.WithScope("ACCESS_AS_USER", ScopeDescription));
        Assert.Throws<ArgumentException>(() => api.WithScope("other", ScopeDescription, scope => scope.Id = id));
        Assert.Single(api.Resource.Scopes);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("relative/path")]
    [InlineData("api://contoso?x=1")]
    [InlineData("api://contoso#fragment")]
    public void WithApplicationIdUri_RejectsInvalidUris(string uri)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api");

        Assert.Throws<ArgumentException>(() => api.WithApplicationIdUri(uri));
        Assert.Null(api.Resource.ApplicationIdUri);
    }

    [Fact]
    public async Task WithApiPermissions_Declares_ButDoesNotGrantOrConsent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("offline_access"),
                EntraIdApiPermission.Application("User.Read.All"));

        var bicep = web.Resource.GetBicepTemplateString();

        Assert.DoesNotContain("oauth2PermissionGrants", bicep);
        Assert.DoesNotContain("appRoleAssignedTo", bicep);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAdminConsent_GrantsDelegatedScopesToAllUsersAndAppRolesToTheServicePrincipal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("other", "Another scope.");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user", "other")
            .WithAdminConsent(api, "access_as_user")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("User.Read.All"),
                EntraIdApiPermission.Application("User.Read.All"),
                EntraIdApiPermission.Application("Directory.Read.All"))
            .WithAdminConsent(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Application("User.Read.All"));

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public async Task WithApiPermissions_ExistingApi_UsesConfiguredScopeIdsAndClientId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var clientIdParameter = builder.AddParameter("api-client-id");
        var existingApi = builder.AddExistingEntraApplication("existing-api", builder.AddParameter("api-tenant-id"), clientIdParameter)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.Parse("11112222-aaaa-3333-bbbb-4444cccc5555"));
        var literalApi = builder.AddExistingEntraApplication("literal-api", TenantId, ClientId)
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.Parse("22223333-aaaa-4444-bbbb-5555cccc6666"));
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(existingApi, "orders.read")
            .WithApiPermissions(literalApi, "items.read")
            .WithAdminConsent(existingApi, "orders.read");

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void ExistingApplication_ExposedApiIsConfigurationOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.NewGuid());

        Assert.Equal("api://contoso-orders", existingApi.Resource.ApplicationIdUri);
        Assert.Throws<InvalidOperationException>(existingApi.Resource.GetBicepTemplateString);
    }

    [Fact]
    public void WithApiPermissions_ExistingApiScopeWithoutIdThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingApi = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId)
            .WithScope("orders.read", "Read orders.");
        var web = builder.AddEntraIdApplication("entra-web");

        var exception = Assert.Throws<InvalidOperationException>(() => web.WithApiPermissions(existingApi, "orders.read"));
        Assert.Contains("EntraIdScope.Id", exception.Message);
        Assert.Empty(web.Resource.ApiAccess);
    }

    [Fact]
    public void WithApiPermissions_ValidatesScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("retired", "Retired.", scope => scope.IsEnabled = false);
        var web = builder.AddEntraIdApplication("entra-web");

        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "unknown"));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "ACCESS_AS_USER"));
        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(api, "retired"));
        Assert.Throws<ArgumentException>(() => api.WithApiPermissions(api, "access_as_user"));
        Assert.Empty(web.Resource.ApiAccess);
    }

    [Fact]
    public void WithApiPermissions_DeduplicatesPermissions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"))
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("user.read"), EntraIdApiPermission.Delegated("User.Read", new Guid("e1fe6dd8-ba31-4d61-89e7-88639da4683d")));

        var access = Assert.Single(web.Resource.ApiAccess);
        var permission = Assert.Single(access.Declared);
        Assert.Equal("User.Read", permission.Name);
    }

    [Fact]
    public void WithApiPermissions_DelegatedAndApplicationPermissionsWithTheSameNameStayDistinct()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("User.Read.All"),
                EntraIdApiPermission.Application("User.Read.All"));

        var declared = Assert.Single(web.Resource.ApiAccess).Declared;
        Assert.Equal(2, declared.Count);
        Assert.Equal(Guid.Parse("a154be20-db9c-4678-8ab7-66f6cc099a59"), Assert.Single(declared, p => p.Type == EntraIdPermissionType.Delegated).Id);
        Assert.Equal(Guid.Parse("df021288-bdef-4463-88db-98f22de89214"), Assert.Single(declared, p => p.Type == EntraIdPermissionType.Application).Id);
    }

    [Fact]
    public void WithApiPermissions_RejectsUnknownPermissionsAndMismatchedIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");

        var unknown = Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("Not.A.Permission")));
        Assert.Contains("Not.A.Permission", unknown.Message);
        Assert.Contains("User.Read", unknown.Message);

        // The application permission exists only as a delegated one in the catalog's ARM entry.
        Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.AzureResourceManager, EntraIdApiPermission.Application("user_impersonation")));

        var mismatch = Assert.Throws<ArgumentException>(() =>
            web.WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read", Guid.NewGuid())));
        Assert.Contains("e1fe6dd8-ba31-4d61-89e7-88639da4683d", mismatch.Message);

        Assert.Throws<ArgumentException>(() => web.WithApiPermissions(EntraIdApi.MicrosoftGraph));
        Assert.Throws<ArgumentException>(() => EntraIdApiPermission.Delegated("User.Read", Guid.Empty));
        Assert.Empty(web.Resource.ApiAccess);
    }

    [Fact]
    public async Task WithApiPermissions_AcceptsPermissionsOutsideTheCatalogById()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mailSend = Guid.Parse("b633e1c5-b582-4048-a93e-9f11b44c7e96");
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Application("Mail.Send", mailSend))
            .WithApiPermissions(
                EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555"),
                EntraIdApiPermission.Delegated("Contoso.Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777")))
            .WithAdminConsent(
                EntraIdApi.FromApplicationId("Contoso API", "99998888-aaaa-7777-bbbb-6666cccc5555"),
                EntraIdApiPermission.Delegated("Contoso.Read", Guid.Parse("33334444-aaaa-5555-bbbb-6666cccc7777")));

        await Verify(web.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void EntraIdApi_FromApplicationId_RequiresIdsAndAGuid()
    {
        var custom = EntraIdApi.FromApplicationId("Contoso API", "99998888-AAAA-7777-bbbb-6666cccc5555");

        Assert.Equal("99998888-aaaa-7777-bbbb-6666cccc5555", custom.ApplicationId);
        Assert.Throws<ArgumentException>(() => EntraIdApi.FromApplicationId("Contoso API", "not-a-guid"));

        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var exception = Assert.Throws<ArgumentException>(() => web.WithApiPermissions(custom, EntraIdApiPermission.Delegated("Contoso.Read")));
        Assert.Contains("Contoso.Read", exception.Message);
    }

    [Fact]
    public void WellKnownApis_HaveStableApplicationIds()
    {
        Assert.Equal("00000003-0000-0000-c000-000000000000", EntraIdApi.MicrosoftGraph.ApplicationId);
        Assert.Equal("797f4846-ba00-4fd7-ba43-dac1f8f63013", EntraIdApi.AzureResourceManager.ApplicationId);
        Assert.Equal("e406a681-f3d4-42a8-90b6-c2b029497af1", EntraIdApi.AzureStorage.ApplicationId);
    }

    [Fact]
    public void WithAdminConsent_RequiresTheSamePermissionToBeDeclaredFirst()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));

        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(api, "access_as_user"));
        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read.All")));

        // Declaring a delegated permission does not declare the application permission with the same name.
        Assert.Throws<InvalidOperationException>(() => web.WithAdminConsent(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Application("Mail.Read")));
        Assert.Empty(Assert.Single(web.Resource.ApiAccess).Consented);
    }

    [Fact]
    public async Task WithPreAuthorizedApplication_WritesClientIdsAndScopeIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var existingClient = builder.AddExistingEntraApplication("existing-client", TenantId, ClientId);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("other", "Another scope.")
            .WithPreAuthorizedApplication(web, "access_as_user")
            .WithPreAuthorizedApplication(web, "other")
            .WithPreAuthorizedApplication(existingClient, "access_as_user");

        Assert.Equal(2, api.Resource.PreAuthorizedClients.Count);
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void WithPreAuthorizedApplication_ValidatesScopes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddEntraIdApplication("entra-web");
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithScope("retired", "Retired.", scope => scope.IsEnabled = false);

        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web));
        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web, "unknown"));
        Assert.Throws<ArgumentException>(() => api.WithPreAuthorizedApplication(web, "retired"));
        Assert.Empty(api.Resource.PreAuthorizedClients);
    }

    [Fact]
    public void PreAuthorizationAndDeclarationBetweenTheSameRegistrationsCannotBothBeUsed()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(api, "access_as_user");

        var exception = Assert.Throws<InvalidOperationException>(() => api.WithPreAuthorizedApplication(web, "access_as_user"));
        Assert.Contains("not both", exception.Message);
        Assert.Empty(api.Resource.PreAuthorizedClients);

        var other = builder.AddEntraIdApplication("other-api").WithScope("access_as_user", ScopeDescription);
        var otherWeb = builder.AddEntraIdApplication("other-web");
        other.WithPreAuthorizedApplication(otherWeb, "access_as_user");
        Assert.Throws<InvalidOperationException>(() => otherWeb.WithApiPermissions(other, "access_as_user"));
        Assert.Empty(otherWeb.Resource.ApiAccess);
    }

    [Fact]
    public void DependencyCyclesThroughOtherRegistrationsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var a = builder.AddEntraIdApplication("a").WithScope("scope", ScopeDescription);
        var b = builder.AddEntraIdApplication("b").WithScope("scope", ScopeDescription);
        var c = builder.AddEntraIdApplication("c").WithScope("scope", ScopeDescription);
        a.WithApiPermissions(b, "scope");
        b.WithApiPermissions(c, "scope");

        Assert.Throws<InvalidOperationException>(() => c.WithApiPermissions(a, "scope"));
        Assert.Throws<InvalidOperationException>(() => c.WithPreAuthorizedApplication(a, "scope"));
    }

    [Fact]
    public async Task ExistingRegistrationsDoNotCreateDependencyCycles()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingWeb = builder.AddExistingEntraApplication("existing-web", TenantId, ClientId);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription)
            .WithPreAuthorizedApplication(existingWeb, "access_as_user");

        // The existing registration deploys nothing, so it waits for nothing and the API's module can depend on its client ID.
        existingWeb.WithApiPermissions(api, "access_as_user");
        await Verify(api.Resource.GetBicepTemplateString(), "bicep");
    }

    [Fact]
    public void GetScopeUri_BuildsTheFullScopeFromTheApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var created = builder.AddEntraIdApplication("created-api").WithScope("access_as_user", ScopeDescription);
        var custom = builder.AddEntraIdApplication("custom-api").WithApplicationIdUri("api://contoso-orders").WithScope("orders.read", "Read orders.");
        var existing = builder.AddExistingEntraApplication("existing-api", TenantId, ClientId).WithApplicationIdUri("api://existing");
        existing.WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var existingWithoutUri = builder.AddExistingEntraApplication("no-uri-api", TenantId, ClientId);
        existingWithoutUri.WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());

        Assert.Equal("{created-api.outputs.applicationIdUri}/access_as_user", created.Resource.GetScopeUri("access_as_user").ValueExpression);
        Assert.Equal("api://contoso-orders/orders.read", custom.Resource.GetScopeUri("orders.read").ValueExpression);
        Assert.Equal("api://existing/items.read", existing.Resource.GetScopeUri("items.read").ValueExpression);
        Assert.Throws<ArgumentException>(() => created.Resource.GetScopeUri("unknown"));
        Assert.Throws<InvalidOperationException>(() => existingWithoutUri.Resource.GetScopeUri("items.read"));
    }

    [Fact]
    public async Task Publish_WiresApiOutputsIntoTheCallerModuleAndPreAuthorizedClientsIntoTheApiModule()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper, workspace.Path);
        var api = builder.AddEntraIdApplication("entra-api")
            .WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(api, "access_as_user")
            .WithAdminConsent(api, "access_as_user")
            .WithApiPermissions(EntraIdApi.MicrosoftGraph, EntraIdApiPermission.Delegated("User.Read"));
        var daemon = builder.AddEntraIdApplication("entra-daemon");
        api.WithPreAuthorizedApplication(daemon, "access_as_user");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = model.Resources.OfType<AzureEnvironmentResource>().Single();
        var options = app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>().Value;
        await using var step = await new TestPipelineActivityReporter(outputHelper).CreateStepAsync("Publishing Entra registrations");
        var context = new AzurePublishingContext(
            workspace.Path, options, app.Services, NullLogger<AzurePublishingContext>.Instance, step);

        await context.WriteModelAsync(model, environment);

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-api", "entra-api.bicep")), "bicep")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "entra-web", "entra-web.bicep")), "bicep");
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithReference_FlowsDeclaredApiScopesToDownstreamApis(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddEntraIdApplication("entra-api").WithScope("access_as_user", ScopeDescription);
        var web = builder.AddEntraIdApplication("entra-web").WithApiPermissions(api, "access_as_user");
        foreach (var (entra, prefix) in new[] { (api, "api"), (web, "web") })
        {
            entra.Resource.Outputs["tenantId"] = TenantId;
            entra.Resource.Outputs["clientId"] = ClientId;
            entra.Resource.Outputs["applicationIdUri"] = $"api://{prefix}-{ClientId}";
            entra.Resource.ProvisioningTaskCompletionSource!.SetResult();
        }

        builder.AddResource(new ProjectResource("apiservice")).WithReference(api);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, operation, TestServiceProvider.Instance);

        Assert.Equal(
            operation == DistributedApplicationOperation.Run ? $"api://api-{ClientId}/access_as_user" : "{entra-api.outputs.applicationIdUri}/access_as_user",
            Assert.Single(env, e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Value);
        Assert.Equal("DownstreamApis__apiservice__Scopes__0", Assert.Single(env, e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Key);
        Assert.DoesNotContain("AzureAd__Scopes", env.Keys);
    }

    [Fact]
    public async Task WithReference_FlowsFirstPartyApiPermissionsAsDelegatedAndAppOnlyEntries()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(
                EntraIdApi.MicrosoftGraph,
                EntraIdApiPermission.Delegated("openid"),
                EntraIdApiPermission.Delegated("User.Read"),
                EntraIdApiPermission.Delegated("Mail.Read"),
                EntraIdApiPermission.Application("User.Read.All"))
            .WithApiPermissions(EntraIdApi.AzureStorage, EntraIdApiPermission.Delegated("user_impersonation"));
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);
        var container = builder.AddContainer("worker", "myimage").WithReference(web, connectionName: "ENTRA");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var containerEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DownstreamApis__MicrosoftGraph__Scopes__0"] = "https://graph.microsoft.com/User.Read",
            ["DownstreamApis__MicrosoftGraph__Scopes__1"] = "https://graph.microsoft.com/Mail.Read",
            ["DownstreamApis__MicrosoftGraph__BaseUrl"] = "https://graph.microsoft.com/v1.0",
            ["DownstreamApis__MicrosoftGraphAppOnly__Scopes__0"] = "https://graph.microsoft.com/.default",
            ["DownstreamApis__MicrosoftGraphAppOnly__BaseUrl"] = "https://graph.microsoft.com/v1.0",
            ["DownstreamApis__MicrosoftGraphAppOnly__RequestAppToken"] = "true",
            ["DownstreamApis__AzureStorage__Scopes__0"] = "https://storage.azure.com/user_impersonation"
        }, env.Where(e => e.Key.StartsWith("DownstreamApis__", StringComparison.Ordinal)).ToDictionary());
        Assert.Equal(new Dictionary<string, string>
        {
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_SCOPES_0"] = "https://graph.microsoft.com/User.Read",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_SCOPES_1"] = "https://graph.microsoft.com/Mail.Read",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_BASE_URL"] = "https://graph.microsoft.com/v1.0",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_SCOPES_0"] = "https://graph.microsoft.com/.default",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_BASE_URL"] = "https://graph.microsoft.com/v1.0",
            ["ENTRA_DOWNSTREAM_APIS_MICROSOFT_GRAPH_APP_ONLY_REQUEST_APP_TOKEN"] = "true",
            ["ENTRA_DOWNSTREAM_APIS_AZURE_STORAGE_SCOPES_0"] = "https://storage.azure.com/user_impersonation"
        }, containerEnv.Where(e => e.Key.StartsWith("ENTRA_DOWNSTREAM", StringComparison.Ordinal)).ToDictionary());
    }

    [Fact]
    public async Task WithReference_FlowsScopesOfExistingApisWithAndWithoutAnApplicationIdUri()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var withUri = builder.AddExistingEntraApplication("with-uri", TenantId, ClientId)
            .WithApplicationIdUri("api://contoso-orders")
            .WithScope("orders.read", "Read orders.", scope => scope.Id = Guid.NewGuid());
        var withoutUri = builder.AddExistingEntraApplication("without-uri", TenantId, ClientId)
            .WithScope("items.read", "Read items.", scope => scope.Id = Guid.NewGuid());
        var web = builder.AddEntraIdApplication("entra-web")
            .WithApiPermissions(withUri, "orders.read")
            .WithApiPermissions(withoutUri, "items.read");
        web.Resource.Outputs["tenantId"] = TenantId;
        web.Resource.Outputs["clientId"] = ClientId;
        web.Resource.ProvisioningTaskCompletionSource!.SetResult();
        builder.AddResource(new ProjectResource("orders-svc")).WithReference(withUri);
        builder.AddResource(new ProjectResource("items-svc")).WithReference(withoutUri);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // The consumer's name is used as is, like the resource name in service discovery.
        Assert.Equal("api://contoso-orders/orders.read", env["DownstreamApis__orders-svc__Scopes__0"]);
        Assert.Equal($"api://{ClientId}/items.read", env["DownstreamApis__items-svc__Scopes__0"]);
    }

    [Fact]
    public async Task WithReference_FlowsOnlyEnabledExposedScopesToTheApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var api = builder.AddExistingEntraApplication("entra-api", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid())
            .WithScope("retired", "Retired.", scope => { scope.Id = Guid.NewGuid(); scope.IsEnabled = false; })
            .WithScope("other", "Another scope.", scope => scope.Id = Guid.NewGuid());
        var project = builder.AddResource(new ProjectResource("api")).WithReference(api);
        var container = builder.AddContainer("worker", "myimage").WithReference(api);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var containerEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("access_as_user other", env["AzureAd__Scopes"]);
        Assert.Equal("access_as_user other", containerEnv["ENTRA_API_SCOPES"]);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("DownstreamApis__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithReference_WithoutDeclarationsOrScopesAddsNoApiConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(["AzureAd__ClientId", "AzureAd__Instance", "AzureAd__TenantId"], env.Keys.Order());
    }

    [Fact]
    public async Task WithReference_RejectsApisThatShareADownstreamName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = EntraIdApi.FromApplicationId("My API", "99998888-aaaa-7777-bbbb-6666cccc5555");
        var second = EntraIdApi.FromApplicationId("MyAPI", "11110000-aaaa-7777-bbbb-6666cccc5555");
        var permission = EntraIdApiPermission.Delegated("Read", Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(first, permission)
            .WithApiPermissions(second, permission);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));
        Assert.Contains("EntraIdApi.FromApplicationId", exception.Message);
    }

    [Fact]
    public async Task WithReference_FirstPartyApisWithDifferentDisplayNamesGetSeparateEntries()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = EntraIdApi.FromApplicationId("My API", "99998888-aaaa-7777-bbbb-6666cccc5555");
        var second = EntraIdApi.FromApplicationId("Other API", "11110000-aaaa-7777-bbbb-6666cccc5555");
        var permission = EntraIdApiPermission.Delegated("Read", Guid.NewGuid());
        var web = builder.AddExistingEntraApplication("entra-web", TenantId, ClientId)
            .WithApiPermissions(first, permission)
            .WithApiPermissions(second, permission);
        var project = builder.AddResource(new ProjectResource("web")).WithReference(web);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"api://{first.ApplicationId}/Read", env["DownstreamApis__MyAPI__Scopes__0"]);
        Assert.Equal($"api://{second.ApplicationId}/Read", env["DownstreamApis__OtherAPI__Scopes__0"]);
    }
    [Fact]
    public async Task WithReference_NamesDownstreamApisAfterTheResourcesThatReferenceTheApi()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");

        // The consumers are read when the environment is resolved, so the order of the calls doesn't matter.
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend")).WithReference(webEntra);
        builder.AddResource(new ProjectResource("apiservice")).WithReference(apiEntra);
        builder.AddResource(new ProjectResource("apiservice2")).WithReference(apiEntra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(
            ["DownstreamApis__apiservice2__Scopes__0", "DownstreamApis__apiservice__Scopes__0"],
            env.Keys.Where(k => k.StartsWith("DownstreamApis__", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithReference_FallsBackToTheApiRegistrationNameWhenNothingReferencesIt()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend")).WithReference(webEntra);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"api://{ClientId}/access_as_user", env["DownstreamApis__apiservice_entra__Scopes__0"]);
    }

    [Theory]
    [InlineData("MyDownstreamApis", "MyDownstreamApis__apiservice__Scopes__0")]
    [InlineData("Section:MyApis", "Section__MyApis__apiservice__Scopes__0")]
    public async Task WithReference_UsesTheDownstreamApisConfigKey(string key, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        builder.AddResource(new ProjectResource("apiservice")).WithReference(apiEntra);
        var webFrontend = builder.AddResource(new ProjectResource("webfrontend"))
            .WithReference(webEntra, connectionName: null, downstreamApisConfigKey: key);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webFrontend.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"api://{ClientId}/access_as_user", env[expected]);
        Assert.Single(env.Keys, k => k.Contains("Apis", StringComparison.Ordinal));
        Assert.Equal("AzureAd__ClientId", Assert.Single(env.Keys, k => k.EndsWith("ClientId", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithReference_UsesTheDownstreamApisConfigKeyForOtherLanguages()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiEntra = builder.AddExistingEntraApplication("apiservice-entra", TenantId, ClientId)
            .WithScope("access_as_user", ScopeDescription, scope => scope.Id = Guid.NewGuid());
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId)
            .WithApiPermissions(apiEntra, "access_as_user");
        builder.AddContainer("api-container", "myimage").WithReference(apiEntra);
        var worker = builder.AddContainer("worker", "myimage").WithReference(webEntra, "ENTRA", "MyApis");
        var defaultWorker = builder.AddContainer("default-worker", "myimage").WithReference(webEntra, "ENTRA");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var defaultEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(defaultWorker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"api://{ClientId}/access_as_user", env["ENTRA_MY_APIS_API_CONTAINER_SCOPES_0"]);
        Assert.Equal($"api://{ClientId}/access_as_user", defaultEnv["ENTRA_DOWNSTREAM_APIS_API_CONTAINER_SCOPES_0"]);
    }

    [Fact]
    public void WithReference_RejectsAnEmptyDownstreamApisConfigKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var webEntra = builder.AddExistingEntraApplication("webfrontend-entra", TenantId, ClientId);
        var project = builder.AddResource(new ProjectResource("webfrontend"));

        Assert.Throws<ArgumentException>(() => project.WithReference(webEntra, connectionName: null, downstreamApisConfigKey: ""));
        Assert.Throws<ArgumentException>(() => project.WithReference(webEntra, connectionName: null, downstreamApisConfigKey: " "));
    }
}