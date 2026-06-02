using System.Buffers.Text;
using Azure;
using Corvus.Text.Json;
using TodoApp.Api.BrokerClient;
using BrokerClientModels = TodoApp.Api.BrokerClient.Models;
using Directory = TodoApp.Api.Directory;
using DirectoryModels = TodoApp.Api.Directory.Models;
using DS = TodoApp.Api.DirectoryStorage;

namespace TodoApp.Api.Handlers;

/// <summary>
/// Handles organization creation and membership management.
/// </summary>
public sealed class OrganizationHandler : Directory.IApiOrganizationsHandler
{
    private readonly IApiProvisioningClient _provisioningClient;
    private readonly DirectoryStore _directoryStore;

    public OrganizationHandler(IApiProvisioningClient provisioningClient, DirectoryStore directoryStore)
    {
        _provisioningClient = provisioningClient;
        _directoryStore = directoryStore;
    }

    public async ValueTask<Directory.CreateOrganizationResult> HandleCreateOrganizationAsync(
        Directory.CreateOrganizationParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string sasUrl = (string)parameters.XStorageSasToken;
        Guid orgId = Guid.NewGuid();

        // Write the organization to the catalog
        var (dirBuilder, etag) = await _directoryStore.ReadMutableAsync(sasUrl, workspace, cancellationToken);
        DS.Directory.Mutable directory = dirBuilder.RootElement;

        var updatedBuilder = DS.Directory.CreateBuilder(workspace, (ref b) =>
        {
            b.Create(
                users: DS.Directory.DirectoryUserArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryUser.Mutable existing in directory.Users.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryUser)existing);
                }),
                organizations: DS.Directory.DirectoryOrganizationArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryOrganization.Mutable existing in directory.Organizations.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryOrganization)existing);
                    ab.AddItem(DS.Directory.DirectoryOrganization.Build((ref ob) =>
                    {
                        ob.Create(
                            id: orgId,
                            name: DS.Directory.DirectoryOrganization.NameEntity.From(parameters.Body.Name));
                    }));
                }),
                memberships: DS.Directory.DirectoryMembershipArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryMembership.Mutable existing in directory.Memberships.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryMembership)existing);
                }));
        });

        try
        {
            await _directoryStore.WriteAsync(sasUrl, updatedBuilder, etag, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return Directory.CreateOrganizationResult.Conflict(
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:conflict"u8, status: 409, title: "Concurrent modification — please retry"u8)),
                workspace);
        }

        // Trigger provisioning for org storage
        JsonElement payload = parameters.Body;
        var requestBuilder = BrokerClientModels.ProvisionRequest.CreateBuilder(
            workspace,
            callbackUrl: "/callbacks/storage-provisioned"u8,
            payload: payload);

        BrokerClientModels.ProvisionRequest request = requestBuilder.RootElement;
        var response = await ProvisionOrgStorageAsync(orgId, request, cancellationToken);

        Guid provisioningTicket = response.TryGetAccepted(out var accepted)
            ? (Guid)accepted.Ticket
            : default;

        return Directory.CreateOrganizationResult.Accepted(
            body: DirectoryModels.Organization.Build(
                (ref b) =>
                {
                    b.Create(
                        id: orgId,
                        name: DirectoryModels.JsonString.From(parameters.Body.Name),
                        status: "provisioning"u8,
                        provisioningTicket: provisioningTicket != default ? provisioningTicket : default);
                }),
            workspace: workspace);
    }

    public async ValueTask<Directory.AddOrganizationMemberResult> HandleAddOrganizationMemberAsync(
        Directory.AddOrganizationMemberParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string sasUrl = (string)parameters.XStorageSasToken;
        if (!TryGetGuid(parameters.OrgId, out Guid orgId) ||
            !parameters.Body.UserId.TryGetValue(out Guid userId))
        {
            return Directory.AddOrganizationMemberResult.Default(
                400,
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:bad-request"u8, status: 400, title: "Invalid member identifier"u8)),
                workspace);
        }

        // Read the catalog
        var (dirBuilder, etag) = await _directoryStore.ReadMutableAsync(sasUrl, workspace, cancellationToken);
        DS.Directory.Mutable directory = dirBuilder.RootElement;

        var (orgHasMembers, requesterIsAdmin) = DirectoryStore.IsInOrganizationRole(
            directory, parameters.OrgId, parameters.XIdentitySubject, "admin"u8);

        bool membershipAlreadyExists = DirectoryStore.IsMemberOfOrganization(
            directory, parameters.OrgId, parameters.Body.UserId);

        // Allow bootstrap (first member of a new org) or require admin
        if (orgHasMembers && !requesterIsAdmin)
        {
            return Directory.AddOrganizationMemberResult.Default(
                403,
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:forbidden"u8, status: 403, title: "Only organization admins can add members"u8)),
                workspace);
        }

        if (membershipAlreadyExists)
        {
            return Directory.AddOrganizationMemberResult.Conflict(
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:conflict"u8, status: 409, title: "User is already a member of this organization"u8)),
                workspace);
        }

        var updatedBuilder = DS.Directory.CreateBuilder(workspace, (ref b) =>
        {
            b.Create(
                users: DS.Directory.DirectoryUserArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryUser.Mutable existing in directory.Users.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryUser)existing);
                }),
                organizations: DS.Directory.DirectoryOrganizationArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryOrganization.Mutable existing in directory.Organizations.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryOrganization)existing);
                }),
                memberships: DS.Directory.DirectoryMembershipArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryMembership.Mutable existing in directory.Memberships.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryMembership)existing);
                    ab.AddItem(DS.Directory.DirectoryMembership.Build((ref mb) =>
                    {
                        mb.Create(
                            orgId: orgId,
                            role: parameters.Body.Role.IsUndefined()
                                ? "member"u8
                                : DS.Directory.DirectoryMembership.RoleEntity.From(parameters.Body.Role),
                            userId: DS.JsonString.From(parameters.Body.UserId));
                    }));
                }));
        });

        try
        {
            await _directoryStore.WriteAsync(sasUrl, updatedBuilder, etag, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return Directory.AddOrganizationMemberResult.Conflict(
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:conflict"u8, status: 409, title: "Concurrent modification — please retry"u8)),
                workspace);
        }

        // Trigger provisioning for org-member storage
        JsonElement payload = parameters.Body;
        var requestBuilder = BrokerClientModels.ProvisionRequest.CreateBuilder(
            workspace,
            callbackUrl: "/callbacks/storage-provisioned"u8,
            payload: payload);

        BrokerClientModels.ProvisionRequest request = requestBuilder.RootElement;
        var response = await ProvisionOrgMemberStorageAsync(
            orgId,
            userId,
            request,
            cancellationToken);

        Guid provisioningTicket = response.TryGetAccepted(out var accepted)
            ? (Guid)accepted.Ticket
            : default;

        return Directory.AddOrganizationMemberResult.Ok(
            body: DirectoryModels.OrganizationMember.Build(
                (ref b) =>
                {
                    b.Create(
                        orgId: orgId,
                        userId: userId,
                        role: parameters.Body.Role.IsUndefined()
                            ? "member"u8
                            : DirectoryModels.OrganizationMember.RoleEntity.From(parameters.Body.Role),
                        provisioningTicket: provisioningTicket != default ? provisioningTicket : default);
                }),
            workspace: workspace);
    }

    private ValueTask<ProvisionOrgStorageResponse> ProvisionOrgStorageAsync(
        Guid orgId,
        BrokerClientModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        return _provisioningClient.ProvisionOrgStorageAsync(
            orgId,
            request,
            cancellationToken);
    }

    private ValueTask<ProvisionOrgMemberStorageResponse> ProvisionOrgMemberStorageAsync(
        Guid orgId,
        Guid userId,
        BrokerClientModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        return _provisioningClient.ProvisionOrgMemberStorageAsync(
            orgId,
            userId,
            request,
            cancellationToken);
    }

    private static bool TryGetGuid(DirectoryModels.JsonString value, out Guid result)
    {
        using UnescapedUtf8JsonString utf8 = value.GetUtf8String();
        return Utf8Parser.TryParse(utf8.Span, out result, out int bytesConsumed) &&
            bytesConsumed == utf8.Span.Length;
    }
}
