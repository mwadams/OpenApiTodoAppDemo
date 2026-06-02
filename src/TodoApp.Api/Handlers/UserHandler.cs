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
/// Handles user creation and profile management.
/// </summary>
public sealed class UserHandler : Directory.IApiUsersHandler
{
    private readonly IApiProvisioningClient _provisioningClient;
    private readonly DirectoryStore _directoryStore;

    public UserHandler(IApiProvisioningClient provisioningClient, DirectoryStore directoryStore)
    {
        _provisioningClient = provisioningClient;
        _directoryStore = directoryStore;
    }

    public async ValueTask<Directory.CreateUserResult> HandleCreateUserAsync(
        Directory.CreateUserParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        if (!TryGetGuid(parameters.XIdentitySubject, out Guid userId))
        {
            return Directory.CreateUserResult.BadRequest(
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:bad-request"u8, status: 400, title: "Invalid authenticated user identifier"u8)),
                workspace);
        }

        string sasUrl = (string)parameters.XStorageSasToken;
        var (dirBuilder, etag) = await _directoryStore.ReadMutableAsync(sasUrl, workspace, cancellationToken);
        DS.Directory.Mutable directory = dirBuilder.RootElement;

        DS.Directory.DirectoryUser.Mutable foundUser = DirectoryStore.FindUserById(directory, parameters.XIdentitySubject);
        if (!foundUser.IsUndefined())
        {
            return Directory.CreateUserResult.Ok(
                body: DirectoryModels.User.Build((ref b) =>
                {
                    b.Create(
                        id: userId,
                        displayName: DirectoryModels.JsonString.From(foundUser.DisplayName),
                        status: "active"u8,
                        email: foundUser.Email.IsUndefined()
                            ? default
                            : DirectoryModels.JsonEmail.From(foundUser.Email));
                }),
                workspace: workspace);
        }

        DS.Directory.DirectoryUser.Mutable staleUser = parameters.Body.Email.IsUndefined()
            ? default
            : DirectoryStore.FindUserByEmail(directory, parameters.Body.Email);

        var subSource = DS.JsonString.From(parameters.XIdentitySubject);
        var displayNameSource = DS.Directory.DirectoryUser.DisplayNameEntity.From(parameters.Body.DisplayName);
        var emailSource = parameters.Body.Email.IsUndefined()
            ? default
            : DS.JsonEmail.From(parameters.Body.Email);

        var updatedBuilder = DS.Directory.CreateBuilder(workspace, (ref b) =>
        {
            b.Create(
                users: DS.Directory.DirectoryUserArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryUser.Mutable existing in directory.Users.EnumerateArray())
                    {
                        if (!staleUser.IsUndefined() && DirectoryStore.JsonStringValueEquals(existing.Id, staleUser.Id))
                        {
                            ab.AddItem(DS.Directory.DirectoryUser.Build((ref ub) =>
                            {
                                ub.Create(id: subSource, displayName: displayNameSource, email: emailSource);
                            }));
                        }
                        else
                        {
                            ab.AddItem((DS.Directory.DirectoryUser)existing);
                        }
                    }

                    if (staleUser.IsUndefined())
                    {
                        ab.AddItem(DS.Directory.DirectoryUser.Build((ref ub) =>
                        {
                            ub.Create(id: subSource, displayName: displayNameSource, email: emailSource);
                        }));
                    }
                }),
                organizations: DS.Directory.DirectoryOrganizationArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryOrganization.Mutable existing in directory.Organizations.EnumerateArray()) ab.AddItem((DS.Directory.DirectoryOrganization)existing);
                }),
                memberships: DS.Directory.DirectoryMembershipArray.Build((ref ab) =>
                {
                    foreach (DS.Directory.DirectoryMembership.Mutable existing in directory.Memberships.EnumerateArray())
                    {
                        if (!staleUser.IsUndefined() && DirectoryStore.JsonStringValueEquals(existing.UserId, staleUser.Id))
                        {
                            ab.AddItem(DS.Directory.DirectoryMembership.Build((ref mb) =>
                            {
                                mb.Create(userId: subSource, orgId: DS.JsonUuid.From(existing.OrgId), role: DS.Directory.DirectoryMembership.RoleEntity.From(existing.Role));
                            }));
                        }
                        else
                        {
                            ab.AddItem((DS.Directory.DirectoryMembership)existing);
                        }
                    }
                }));
        });

        try
        {
            await _directoryStore.WriteAsync(sasUrl, updatedBuilder, etag, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return Directory.CreateUserResult.Conflict(
                DirectoryModels.ProblemDetails.Build((ref b) =>
                    b.Create(type: "urn:todo-app:conflict"u8, status: 409, title: "Concurrent modification — please retry"u8)),
                workspace);
        }

        JsonElement payload = parameters.Body;
        var requestBuilder = BrokerClientModels.ProvisionRequest.CreateBuilder(
            workspace,
            callbackUrl: "/callbacks/storage-provisioned"u8,
            payload: payload);

        BrokerClientModels.ProvisionRequest request = requestBuilder.RootElement;
        var response = await ProvisionUserStorageAsync(userId, request, cancellationToken);

        Guid provisioningTicket = response.TryGetAccepted(out var accepted)
            ? (Guid)accepted.Ticket
            : default;

        return Directory.CreateUserResult.Accepted(
            body: DirectoryModels.User.Build(
                (ref b) =>
                {
                    b.Create(
                        id: userId,
                        displayName: DirectoryModels.JsonString.From(parameters.Body.DisplayName),
                        status: "provisioning"u8,
                        email: parameters.Body.Email.IsUndefined()
                            ? default
                            : DirectoryModels.JsonEmail.From(parameters.Body.Email),
                        provisioningTicket: provisioningTicket != default ? provisioningTicket : default);
                }),
            workspace: workspace);
    }

    private ValueTask<ProvisionUserStorageResponse> ProvisionUserStorageAsync(
        Guid userId,
        BrokerClientModels.ProvisionRequest request,
        CancellationToken cancellationToken)
    {
        return _provisioningClient.ProvisionUserStorageAsync(
            userId,
            request,
            cancellationToken);
    }

    public async ValueTask<Directory.GetCurrentUserProfileResult> HandleGetCurrentUserProfileAsync(
        Directory.GetCurrentUserProfileParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string sasUrl = (string)parameters.XStorageSasToken;

        var (dirBuilder, _) = await _directoryStore.ReadMutableAsync(sasUrl, workspace, cancellationToken);
        DS.Directory.Mutable directory = dirBuilder.RootElement;

        var foundUser = DirectoryStore.FindUserById(directory, parameters.XIdentitySubject);

        if (foundUser.IsUndefined())
        {
            return Directory.GetCurrentUserProfileResult.NotFound(
                DirectoryModels.ProblemDetails.Build((ref b) => b.Create(
                    type: "urn:todo-app:user-not-found"u8, status: 404)),
                workspace);
        }

        return Directory.GetCurrentUserProfileResult.Ok(
            body: BuildUserProfile(parameters.XIdentitySubject, foundUser, directory),
            workspace: workspace);
    }

    public async ValueTask<Directory.ListCurrentUserOrganizationsResult> HandleListCurrentUserOrganizationsAsync(
        Directory.ListCurrentUserOrganizationsParams parameters, JsonWorkspace workspace, CancellationToken cancellationToken)
    {
        string sasUrl = (string)parameters.XStorageSasToken;

        var (dirBuilder, _) = await _directoryStore.ReadMutableAsync(sasUrl, workspace, cancellationToken);
        DS.Directory.Mutable directory = dirBuilder.RootElement;

        return Directory.ListCurrentUserOrganizationsResult.Ok(
            body: BuildMembershipList(parameters.XIdentitySubject, directory),
            workspace: workspace);
    }

    private static DirectoryModels.UserProfile.Source BuildUserProfile<TUserId>(
        TUserId userId,
        DS.Directory.DirectoryUser.Mutable user,
        DS.Directory.Mutable directory)
        where TUserId : struct, Corvus.Text.Json.Internal.IJsonElement<TUserId>
    {
        return DirectoryModels.UserProfile.Build((ref b) =>
        {
            b.Create(
                displayName: DirectoryModels.JsonString.From(user.DisplayName),
                id: DirectoryModels.JsonString.From(userId),
                organizations: BuildMembershipList(userId, directory),
                email: !user.Email.IsUndefined()
                    ? DirectoryModels.JsonEmail.From(user.Email)
                    : default);
        });
    }

    private static DirectoryModels.OrganizationMembershipList.Source BuildMembershipList<TUserId>(
        TUserId userId,
        DS.Directory.Mutable directory)
        where TUserId : struct, Corvus.Text.Json.Internal.IJsonElement<TUserId>
    {
        return DirectoryModels.OrganizationMembershipList.Build(
            (ref b) =>
            {
                using UnescapedUtf8JsonString userIdUtf8 = DirectoryStore.GetUnescapedStringValue(userId);
                foreach (DS.Directory.DirectoryMembership.Mutable m in directory.Memberships.EnumerateArray())
                {
                    if (!m.UserId.ValueEquals(userIdUtf8.Span)) continue;

                    var foundOrg = DirectoryStore.FindOrganizationById(directory, m.OrgId);

                    b.AddItem(DirectoryModels.OrganizationMembership.Build(
                        (ref mb) =>
                        {
                            mb.Create(
                                orgId: DirectoryModels.JsonUuid.From(m.OrgId),
                                orgName: !foundOrg.IsUndefined()
                                    ? DirectoryModels.JsonString.From(foundOrg.Name)
                                    : (DirectoryModels.JsonString.Source)"Unknown",
                                role: DirectoryModels.OrganizationMembership.RoleEntity.From(m.Role));
                        }));
                }
            });
    }

    private static bool TryGetGuid(DirectoryModels.JsonString value, out Guid result)
    {
        result = default;
        if (value.IsUndefined())
        {
            return false;
        }

        using UnescapedUtf8JsonString utf8 = value.GetUtf8String();
        return Utf8Parser.TryParse(utf8.Span, out result, out int bytesConsumed) &&
            bytesConsumed == utf8.Span.Length;
    }
}
