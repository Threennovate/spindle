using System.Net;
using Spindle.Api.Clients;
using Spindle.Api.Projects;
using Spindle.Api.Tags;

namespace Spindle.Tests.Integration;

/// <summary>Clients, projects, tasks and tags.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogTests(PostgresApiFactory factory)
{
    [Fact]
    public async Task Client_names_are_unique_regardless_of_case_but_free_again_after_deletion()
    {
        var user = await TestUser.RegisterAsync(factory);
        var acme = await user.CreateAsync<ClientResponse>("/clients", new { name = "Acme" });

        var duplicate = await user.PostAsync("/clients", new { name = "ACME" });
        (await user.DeleteAsync($"/clients/{acme.Id}")).EnsureSuccessStatusCode();
        var reused = await user.PostAsync("/clients", new { name = "Acme" });

        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
    }

    [Fact]
    public async Task Archived_clients_are_hidden_by_default_and_can_be_restored()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = await user.CreateAsync<ClientResponse>("/clients", new { name = "Dormant" });

        (await user.PostAsync($"/clients/{client.Id}/archive", new { })).EnsureSuccessStatusCode();
        var active = await user.GetAsync<List<ClientResponse>>("/clients");
        var all = await user.GetAsync<List<ClientResponse>>("/clients?includeArchived=true");
        var restored = await TestUser.ReadAsync<ClientResponse>(await user.PostAsync($"/clients/{client.Id}/restore", new { }));

        Assert.Empty(active);
        Assert.NotNull(Assert.Single(all).ArchivedAt);
        Assert.Null(restored.ArchivedAt);
    }

    [Fact]
    public async Task Project_names_are_unique_per_client()
    {
        var user = await TestUser.RegisterAsync(factory);
        var acme = await user.CreateAsync<ClientResponse>("/clients", new { name = "Acme" });
        var globex = await user.CreateAsync<ClientResponse>("/clients", new { name = "Globex" });
        await CreateProjectAsync(user, "Website", acme.Id);

        var sameClient = await user.PostAsync("/projects", Project("Website", acme.Id));
        var otherClient = await user.PostAsync("/projects", Project("Website", globex.Id));
        var noClient = await user.PostAsync("/projects", Project("Website", null));
        var noClientAgain = await user.PostAsync("/projects", Project("Website", null));

        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(sameClient));
        Assert.Equal(HttpStatusCode.Created, otherClient.StatusCode);
        Assert.Equal(HttpStatusCode.Created, noClient.StatusCode);
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(noClientAgain));
    }

    [Fact]
    public async Task Deleting_a_client_keeps_its_projects_without_a_client()
    {
        var user = await TestUser.RegisterAsync(factory);
        var client = await user.CreateAsync<ClientResponse>("/clients", new { name = "Leaving" });
        var project = await CreateProjectAsync(user, "Kept", client.Id);

        (await user.DeleteAsync($"/clients/{client.Id}")).EnsureSuccessStatusCode();

        Assert.Null((await user.GetAsync<ProjectResponse>($"/projects/{project.Id}")).ClientId);
    }

    [Fact]
    public async Task Projects_need_a_valid_color_and_an_active_client()
    {
        var user = await TestUser.RegisterAsync(factory);
        var archived = await user.CreateAsync<ClientResponse>("/clients", new { name = "Archived" });
        (await user.PostAsync($"/clients/{archived.Id}/archive", new { })).EnsureSuccessStatusCode();

        var badColor = await user.PostAsync("/projects", new { name = "Colorless", color = "purple" });
        var archivedClient = await user.PostAsync("/projects", Project("For archived", archived.Id));
        var unknownClient = await user.PostAsync("/projects", Project("For nobody", Guid.NewGuid()));

        Assert.Equal(["color"], await TestUser.ErrorFieldsAsync(badColor));
        Assert.Equal(["clientId"], await TestUser.ErrorFieldsAsync(archivedClient));
        Assert.Equal(["clientId"], await TestUser.ErrorFieldsAsync(unknownClient));
    }

    [Fact]
    public async Task Projects_keep_their_settings_and_tasks()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await user.CreateAsync<ProjectResponse>("/projects", new
        {
            name = "Rebrand",
            color = "#0ea5e9",
            isPrivate = true,
            isBillable = true,
            estimateSeconds = 36_000,
        });
        await user.CreateAsync<TaskResponse>($"/projects/{project.Id}/tasks", new { name = "Logo", estimateSeconds = 7_200 });
        var copy = await user.PostAsync($"/projects/{project.Id}/tasks", new { name = "logo" });

        var stored = await user.GetAsync<ProjectResponse>($"/projects/{project.Id}");

        Assert.Equal("#0EA5E9", stored.Color);
        Assert.True(stored.IsPrivate);
        Assert.True(stored.IsBillable);
        Assert.Equal(36_000, stored.EstimateSeconds);
        Assert.Equal(("Logo", 7_200L), (stored.Tasks.Single().Name, stored.Tasks.Single().EstimateSeconds!.Value));
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(copy));
    }

    [Fact]
    public async Task Done_tasks_can_be_reopened()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await CreateProjectAsync(user, "Checklist", null);
        var task = await user.CreateAsync<TaskResponse>($"/projects/{project.Id}/tasks", new { name = "Ship" });

        var done = await TestUser.ReadAsync<TaskResponse>(await user.PutAsync($"/projects/{project.Id}/tasks/{task.Id}", new { name = "Ship", isDone = true }));
        var reopened = await TestUser.ReadAsync<TaskResponse>(await user.PutAsync($"/projects/{project.Id}/tasks/{task.Id}", new { name = "Ship", isDone = false }));

        Assert.True(done.IsDone);
        Assert.False(reopened.IsDone);
    }

    [Fact]
    public async Task Tags_are_unique_and_can_be_renamed()
    {
        var user = await TestUser.RegisterAsync(factory);
        var tag = await user.CreateAsync<TagResponse>("/tags", new { name = "meeting" });
        await user.CreateAsync<TagResponse>("/tags", new { name = "focus" });

        var duplicate = await user.PostAsync("/tags", new { name = "Meeting" });
        var renamedOntoOther = await user.PutAsync($"/tags/{tag.Id}", new { name = "focus" });
        var renamed = await TestUser.ReadAsync<TagResponse>(await user.PutAsync($"/tags/{tag.Id}", new { name = "meetings" }));

        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(duplicate));
        Assert.Equal(["name"], await TestUser.ErrorFieldsAsync(renamedOntoOther));
        Assert.Equal("meetings", renamed.Name);
    }

    [Fact]
    public async Task Another_workspace_does_not_exist_for_you()
    {
        var user = await TestUser.RegisterAsync(factory);
        var stranger = await TestUser.RegisterAsync(factory);

        var projects = await stranger.Client.GetAsync(user.In("/projects"), TestContext.Current.CancellationToken);
        var unknown = await user.Client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/tags", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, projects.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static object Project(string name, Guid? clientId) => new { name, clientId, color = "#6D28D9" };

    private static Task<ProjectResponse> CreateProjectAsync(TestUser user, string name, Guid? clientId) =>
        user.CreateAsync<ProjectResponse>("/projects", Project(name, clientId));
}
