﻿using System;
using System.Collections.Concurrent;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using Raven.Abstractions;
using Raven.Abstractions.Logging;
using Raven.Database.Server;
using Raven.Database.Server.Abstractions;

namespace Raven.Database.Extensions
{
	public static class AdminFinder
	{
		private static readonly CachingAdminFinder cachingAdminFinder = new CachingAdminFinder();

		public static bool IsAdministrator(this IPrincipal principal, AnonymousUserAccessMode mode)
		{
			if (principal == null || principal.Identity == null | principal.Identity.IsAuthenticated == false)
			{
				if (mode == AnonymousUserAccessMode.Admin)
					return true; 
				return false;
			}

			var databaseAccessPrincipal = principal as PrincipalWithDatabaseAccess;
			var windowsPrincipal = databaseAccessPrincipal == null ? principal as WindowsPrincipal : databaseAccessPrincipal.Principal;
			
			if (windowsPrincipal != null)
			{
				var current = WindowsIdentity.GetCurrent();
				var windowsIdentity = ((WindowsIdentity)windowsPrincipal.Identity);

				// if the request was made using the same user as RavenDB is running as, 
				// we consider this to be an administrator request
				if (current != null && current.User == windowsIdentity.User)
					return true;

				if (windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator))
					return true;

				if (windowsIdentity.User == null)
					return false; // we aren't sure who this use is, probably anonymous?
				// we still need to make this check, to by pass UAC non elevated admin issue
				return cachingAdminFinder.IsAdministrator(windowsIdentity);
			}

			return principal.IsInRole("Administrators");
		}

		public class CachingAdminFinder
		{
			private static readonly ILog log = LogManager.GetCurrentClassLogger();

			private class CachedResult
			{
				public int Usage;
				public DateTime Timestamp;
				public Lazy<bool> Value;
			}

			private const int CacheMaxSize = 1024;
			private static readonly TimeSpan maxDuration = TimeSpan.FromMinutes(15);

			private readonly ConcurrentDictionary<SecurityIdentifier, CachedResult> cache =
				new ConcurrentDictionary<SecurityIdentifier, CachedResult>();

			public bool IsAdministrator(WindowsIdentity windowsIdentity)
			{
				CachedResult value;
				if (cache.TryGetValue(windowsIdentity.User, out value) && (SystemTime.UtcNow - value.Timestamp) <= maxDuration)
				{
					Interlocked.Increment(ref value.Usage);
					return value.Value.Value;
				}

				var cachedResult = new CachedResult
				{
					Usage = value == null ? 1 : value.Usage + 1,
					Value = new Lazy<bool>(() =>
					{
						try
						{
							return IsAdministratorNoCache(windowsIdentity.Name);
						}
						catch (Exception e)
						{
							log.WarnException("Could not determine whatever user is admin or not, assuming not", e);
							return false;
						}
					}),
					Timestamp = SystemTime.UtcNow
				};

				cache.AddOrUpdate(windowsIdentity.User, cachedResult, (_, __) => cachedResult);
				if (cache.Count > CacheMaxSize)
				{
					foreach (var source in cache
							.Where(x => (SystemTime.UtcNow - x.Value.Timestamp) > maxDuration))
					{
						CachedResult ignored;
						cache.TryRemove(source.Key, out ignored);
						log.Debug("Removing expired {0} from cache", source.Key);
					}
					if (cache.Count > CacheMaxSize)
					{
						foreach (var source in cache
						.OrderByDescending(x => x.Value.Usage)
						.ThenBy(x => x.Value.Timestamp)
						.Skip(CacheMaxSize))
						{
							if (source.Key == windowsIdentity.User)
								continue; // we don't want to remove the one we just added
							CachedResult ignored;
							cache.TryRemove(source.Key, out ignored);
							log.Debug("Removing least used {0} from cache", source.Key);
						}
					}
				}

				return cachedResult.Value.Value;
			}

			private static bool IsAdministratorNoCache(string username)
			{
				var ctx = GeneratePrincipalContext();
				var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username);
				if (up != null)
				{
					PrincipalSearchResult<Principal> authGroups = up.GetAuthorizationGroups();
					return authGroups.Any(principal =>
											principal.Sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
											principal.Sid.IsWellKnown(WellKnownSidType.AccountDomainAdminsSid) ||
											principal.Sid.IsWellKnown(WellKnownSidType.AccountAdministratorSid) ||
											principal.Sid.IsWellKnown(WellKnownSidType.AccountEnterpriseAdminsSid));
				}
				return false;
			}

			private static bool? useLocalMachine;
			private static PrincipalContext GeneratePrincipalContext()
			{
				if(useLocalMachine == true)
					return new PrincipalContext(ContextType.Machine);
				try
				{
					if(useLocalMachine == null)
					{
						Domain.GetComputerDomain();
						useLocalMachine = false;
					}
					try
					{
						return new PrincipalContext(ContextType.Domain);
					}
					catch (PrincipalServerDownException)
					{
						// can't access domain, check local machine instead 
						return new PrincipalContext(ContextType.Machine);
					}
				}
				catch (ActiveDirectoryObjectNotFoundException)
				{
					useLocalMachine = true;
					// not in a domain
					return new PrincipalContext(ContextType.Machine);
				}
			}
		}

		public static bool IsAdministrator(this IPrincipal principal, DocumentDatabase database)
		{
			var databaseAccessPrincipal = principal as PrincipalWithDatabaseAccess;
			if (databaseAccessPrincipal != null)
			{
				if (databaseAccessPrincipal.AdminDatabases.Any(name => name == "*")
				    && database.Name != null && database.Name != "<system>")
					return true;
				if (databaseAccessPrincipal.AdminDatabases.Any(name => string.Equals(name, database.Name, StringComparison.InvariantCultureIgnoreCase)))
					return true;
			}

			return false;
		}
	}
}