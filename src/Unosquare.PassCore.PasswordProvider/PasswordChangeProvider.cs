﻿namespace Unosquare.PassCore.PasswordProvider
{
    using Common;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using System;
    using System.DirectoryServices;
    using System.DirectoryServices.AccountManagement;
    using System.Linq;

    public partial class PasswordChangeProvider : IPasswordChangeProvider
    {
        private readonly ILogger _logger;
        private IdentityType _idType = IdentityType.UserPrincipalName;

        public PasswordChangeProvider(
            ILogger<PasswordChangeProvider> logger,
            IOptions<PasswordChangeOptions> options)
        {
            _logger = logger;
            Settings = options.Value;
            SetIdType();
        }

        /// <inheritdoc />
        public IAppSettings Settings { get; }

        public ApiErrorItem PerformPasswordChange(string username, string currentPassword, string newPassword)
        {
            var options = Settings as PasswordChangeOptions;

            try
            {
                using (var principalContext = AcquirePrincipalContext())
                {
                    var userPrincipal = UserPrincipal.FindByIdentity(principalContext, _idType, username);

                    // Check if the user principal exists
                    if (userPrincipal == null)
                    {
                        _logger.LogWarning("The User principal doesn't exist");

                        return new ApiErrorItem { ErrorCode = ApiErrorCode.UserNotFound };
                    }

                    ValidateGroups(options, userPrincipal);

                    // Check if password change is allowed
                    if (userPrincipal.UserCannotChangePassword)
                    {
                        _logger.LogWarning("The User principal cannot change the password");

                        return new ApiErrorItem { ErrorCode = ApiErrorCode.ChangeNotPermitted };
                    }

                    // Check if password expired or must be changed
                    if (userPrincipal.LastPasswordSet == null)
                    {
                        _logger.LogWarning("The User principal password have no last password");

                        var der = (DirectoryEntry)userPrincipal.GetUnderlyingObject();
                        var prop = der.Properties["pwdLastSet"];

                        if (prop != null)
                        {
                            prop.Value = -1;
                        }

                        try
                        {
                            der.CommitChanges();
                        }
                        catch (Exception ex)
                        {
                            return new ApiErrorItem { ErrorCode = ApiErrorCode.Generic, Message = ex.Message };
                        }
                    }

                    // Use always UPN for passwordcheck.
                    if (ValidateUserCredentials(userPrincipal.UserPrincipalName, currentPassword, principalContext) ==
                        false)
                    {
                        _logger.LogWarning("The User principal password is not valid");

                        return new ApiErrorItem { ErrorCode = ApiErrorCode.InvalidCredentials };
                    }

                    // Change the password via 2 different methods. Try SetPassword if ChangePassword fails.
                    try
                    {
                        // Try by regular ChangePassword method
                        userPrincipal.ChangePassword(currentPassword, newPassword);
                    }
                    catch
                    {
                        if (options.UseAutomaticContext)
                        {
                            _logger.LogWarning("The User principal password cannot be changed and setPassword won't be called");

                            throw;
                        }

                        // If the previous attempt failed, use the SetPassword method.
                        userPrincipal.SetPassword(newPassword);

                        _logger.LogDebug("The User principal password updated with setPassword");
                    }

                    userPrincipal.Save();
                    _logger.LogDebug("The User principal password updated with setPassword");
                }
            }
            catch (Exception ex)
            {
                var item = ex is ApiErrorException apiError
                    ? apiError.ToApiErrorItem()
                    : new ApiErrorItem
                    {
                        ErrorCode = ApiErrorCode.InvalidCredentials,
                        Message = $"Failed to update password: {ex.Message}",
                    };

                _logger.LogWarning(item.Message, ex);

                return item;
            }

            return null;
        }

        private static void ValidateGroups(PasswordChangeOptions options, UserPrincipal userPrincipal)
        {
            if (options.RestrictedADGroups.Any())
            {
                foreach (var userPrincipalAuthGroup in userPrincipal.GetAuthorizationGroups())
                {
                    if (options.RestrictedADGroups.Contains(userPrincipalAuthGroup.Name))
                    {
                        throw new ApiErrorException("The User principal is listed as restricted", ApiErrorCode.ChangeNotPermitted);
                    }
                }
            }

            if (!options.AllowedADGroups.Any()) return;

            foreach (var userPrincipalAuthGroup in userPrincipal.GetAuthorizationGroups())
            {
                if (!options.AllowedADGroups.Contains(userPrincipalAuthGroup.Name))
                {
                    throw new ApiErrorException("The User principal is not listed as allowed", ApiErrorCode.ChangeNotPermitted);
                }
            }
        }

        private static bool ValidateUserCredentials(
            string upn,
            string currentPassword,
            PrincipalContext principalContext)
        {
            if (principalContext.ValidateCredentials(upn, currentPassword))
                return true;

            var tmpAuthority = upn?.Split('@').Last();

            if (LogonUser(upn, tmpAuthority, currentPassword, LogonTypes.Network, LogonProviders.Default, out _))
                return true;

            var errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            // Both of these means that the password CAN change and that we got the correct password
            return errorCode == ERROR_PASSWORD_MUST_CHANGE || errorCode == ERROR_PASSWORD_EXPIRED;
        }

        /// <summary>
        /// Use the values from appsettings.IdTypeForUser as fault-tolerant as possible
        /// </summary>
        private void SetIdType()
        {
            var idType = (Settings as PasswordChangeOptions)?.IdTypeForUser;

            if (string.IsNullOrWhiteSpace(idType))
            {
                _idType = IdentityType.UserPrincipalName;
            }
            else
            {
                var tmpIdType = idType.Trim().ToLower();

                switch (tmpIdType)
                {
                    case "distinguishedname":
                    case "distinguished name":
                    case "dn":
                        _idType = IdentityType.DistinguishedName;
                        break;
                    case "globally unique identifier":
                    case "globallyuniqueidentifier":
                    case "guid":
                        _idType = IdentityType.Guid;
                        break;
                    case "name":
                    case "nm":
                        _idType = IdentityType.Name;
                        break;
                    case "samaccountname":
                    case "accountname":
                    case "sam account":
                    case "sam account name":
                    case "sam":
                        _idType = IdentityType.SamAccountName;
                        break;
                    case "securityidentifier":
                    case "securityid":
                    case "secid":
                    case "security identifier":
                    case "sid":
                        _idType = IdentityType.Sid;
                        break;
                    default:
                        _idType = IdentityType.UserPrincipalName;
                        break;
                }
            }
        }

        private PrincipalContext AcquirePrincipalContext()
        {
            return (Settings as PasswordChangeOptions)?.UseAutomaticContext == true
                ? new PrincipalContext(ContextType.Domain)
                : new PrincipalContext(
                    ContextType.Domain,
                    $"{Settings.LdapHostnames.First()}:{Settings.LdapPort}",
                    Settings.LdapUsername,
                    Settings.LdapPassword);
        }
    }
}