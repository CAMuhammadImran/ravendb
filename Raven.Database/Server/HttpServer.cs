//-----------------------------------------------------------------------
// <copyright file="HttpServer.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.MEF;
using Raven.Database.Config;
using Raven.Database.Exceptions;
using Raven.Database.Extensions;
using Raven.Database.Plugins.Builtins;
using Raven.Database.Server.Abstractions;
using Raven.Database.Server.Security;
using Raven.Database.Server.Security.OAuth;
using Raven.Database.Server.Security.Windows;
using Raven.Database.Util;

namespace Raven.Database.Server
{
	public class HttpServer : IDisposable
	{
		private const int MaxConcurrentRequests = 192;
		public DocumentDatabase DefaultResourceStore { get; private set; }
		public InMemoryRavenConfiguration DefaultConfiguration { get; private set; }
		readonly AbstractRequestAuthorizer requestAuthorizer;

		private readonly ThreadLocal<string> currentTenantId = new ThreadLocal<string>();
		private readonly ThreadLocal<DocumentDatabase> currentDatabase = new ThreadLocal<DocumentDatabase>();
		private readonly ThreadLocal<InMemoryRavenConfiguration> currentConfiguration = new ThreadLocal<InMemoryRavenConfiguration>();

		protected readonly ConcurrentDictionary<string, DocumentDatabase> ResourcesStoresCache =
			new ConcurrentDictionary<string, DocumentDatabase>(StringComparer.InvariantCultureIgnoreCase);

		private readonly ConcurrentDictionary<string, DateTime> databaseLastRecentlyUsed = new ConcurrentDictionary<string, DateTime>(StringComparer.InvariantCultureIgnoreCase);


		public int NumberOfRequests
		{
			get { return Thread.VolatileRead(ref physicalRequestsCount); }
		}

		[ImportMany]
		public OrderedPartCollection<AbstractRequestResponder> RequestResponders { get; set; }

		[ImportMany]
		public OrderedPartCollection<IConfigureHttpListener> ConfigureHttpListeners { get; set; }

		public InMemoryRavenConfiguration Configuration
		{
			get
			{
				return DefaultConfiguration;
			}
		}

		private static readonly Regex databaseQuery = new Regex("^/databases/([^/]+)(?=/?)", RegexOptions.IgnoreCase);


		private HttpListener listener;

		private static readonly Logger logger = LogManager.GetCurrentClassLogger();

		private int reqNum;


		// concurrent requests
		// we set 1/4 aside for handling background tasks
		private readonly SemaphoreSlim concurretRequestSemaphore = new SemaphoreSlim(MaxConcurrentRequests);
		private Timer databasesCleanupTimer;
		private int physicalRequestsCount;

		public bool HasPendingRequests
		{
			get { return concurretRequestSemaphore.CurrentCount != MaxConcurrentRequests; }
		}

		public HttpServer(InMemoryRavenConfiguration configuration, DocumentDatabase resourceStore)
		{
			RegisterHttpEndpointTarget();

			DefaultResourceStore = resourceStore;
			DefaultConfiguration = configuration;

			configuration.Container.SatisfyImportsOnce(this);

			foreach (var responder in RequestResponders)
			{
				responder.Value.Initialize(() => currentDatabase.Value, () => currentConfiguration.Value, () => currentTenantId.Value, this);
			}

			switch (configuration.AuthenticationMode.ToLowerInvariant())
			{
				case "windows":
					requestAuthorizer = new WindowsRequestAuthorizer();
					break;
				case "oauth":
					requestAuthorizer = new OAuthRequestAuthorizer();
					break;
				default:
					throw new InvalidOperationException(
						string.Format("Unknown AuthenticationMode {0}. Options are Windows and OAuth", configuration.AuthenticationMode));
			}

			requestAuthorizer.Initialize(() => currentDatabase.Value, () => currentConfiguration.Value, () => currentTenantId.Value, this);
			RemoveTenantDatabase.Occured.Subscribe(TenantDatabaseRemoved);
		}

		public static void RegisterHttpEndpointTarget()
		{
			Type type;
			if (ConfigurationItemFactory.Default.Targets.TryGetDefinition("HttpEndpoint", out type) == false)
				ConfigurationItemFactory.Default.Targets.RegisterDefinition("HttpEndpoint", typeof(BoundedMemoryTarget));
		}


		private void TenantDatabaseRemoved(object sender, RemoveTenantDatabase.Event @event)
		{
			if (@event.Database != DefaultResourceStore)
				return; // we ignore anything that isn't from the root db

			CleanupDatabase(@event.Name);
		}

		#region IDisposable Members

		public void Dispose()
		{
			databasesCleanupTimer.Dispose();
			if (listener != null && listener.IsListening)
				listener.Stop();
			currentConfiguration.Dispose();
			currentDatabase.Dispose();
			currentTenantId.Dispose();
			foreach (var documentDatabase in ResourcesStoresCache)
			{
				documentDatabase.Value.Dispose();
			}
		}

		#endregion

		public void Start()
		{
			listener = new HttpListener();
			string virtualDirectory = DefaultConfiguration.VirtualDirectory;
			if (virtualDirectory.EndsWith("/") == false)
				virtualDirectory = virtualDirectory + "/";
			listener.Prefixes.Add("http://" + (DefaultConfiguration.HostName ?? "+") + ":" + DefaultConfiguration.Port + virtualDirectory);

			foreach (var configureHttpListener in ConfigureHttpListeners)
			{
				configureHttpListener.Value.Configure(listener, DefaultConfiguration);
			}

			databasesCleanupTimer = new Timer(CleanupDatabases, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
			listener.Start();
			listener.BeginGetContext(GetContext, null);
		}

		private void CleanupDatabases(object state)
		{
			var databasesToCleanup = databaseLastRecentlyUsed
				.Where(x => (SystemTime.Now - x.Value).TotalMinutes > 10)
				.Select(x => x.Key)
				.ToArray();

			foreach (var db in databasesToCleanup)
			{
				// intentionally inside the loop, so we get better concurrency overall
				// since shutting down a database can take a while
				CleanupDatabase(db);

			}
		}

		protected void CleanupDatabase(string db)
		{
			lock (ResourcesStoresCache)
			{
				DateTime time;
				databaseLastRecentlyUsed.TryRemove(db, out time);

				DocumentDatabase database;
				if (ResourcesStoresCache.TryRemove(db, out database))
					database.Dispose();


			}
		}

		private void GetContext(IAsyncResult ar)
		{
			IHttpContext ctx;
			try
			{
				ctx = new HttpListenerContextAdpater(listener.EndGetContext(ar), DefaultConfiguration);
				//setup waiting for the next request
				listener.BeginGetContext(GetContext, null);
			}
			catch (Exception)
			{
				// can't get current request / end new one, probably
				// listner shutdown
				return;
			}
			
			if (concurretRequestSemaphore.Wait(TimeSpan.FromSeconds(5)) == false)
			{
				HandleTooBusyError(ctx);
				return;
			}
			try
			{
				Interlocked.Increment(ref physicalRequestsCount);
				HandleActualRequest(ctx);
			}
			finally
			{
				concurretRequestSemaphore.Release();
			}
		}

		public void HandleActualRequest(IHttpContext ctx)
		{
			var sw = Stopwatch.StartNew();
			bool ravenUiRequest = false;
			try
			{
				ravenUiRequest = DispatchRequest(ctx);
			}
			catch (Exception e)
			{
				HandleException(ctx, e);
				if (ShouldLogException(e))
					logger.WarnException("Error on request", e);
			}
			finally
			{
				try
				{
					FinalizeRequestProcessing(ctx, sw, ravenUiRequest);
				}
				catch (Exception e)
				{
					logger.ErrorException("Could not finalize request properly", e);
				}
			}
		}

		protected bool ShouldLogException(Exception exception)
		{
			return exception is IndexDisabledException == false &&
			       exception is IndexDoesNotExistsException == false;

		}

		private void FinalizeRequestProcessing(IHttpContext ctx, Stopwatch sw, bool ravenUiRequest)
		{
			LogHttpRequestStatsParams logHttpRequestStatsParam = null;
			try
			{
				logHttpRequestStatsParam = new LogHttpRequestStatsParams(
					sw,
					ctx.Request.Headers,
					ctx.Request.HttpMethod,
					ctx.Response.StatusCode,
					ctx.Request.Url.PathAndQuery);
			}
			catch (Exception e)
			{
				logger.WarnException("Could not gather information to log request stats", e);
			}

			ctx.FinalizeResonse();
			sw.Stop();

			if (ravenUiRequest || logHttpRequestStatsParam == null)
				return;

			LogHttpRequestStats(logHttpRequestStatsParam);
			ctx.OutputSavedLogItems(logger);
		}

		private void LogHttpRequestStats(LogHttpRequestStatsParams logHttpRequestStatsParams)
		{
			// we filter out requests for the UI because they fill the log with information
			// we probably don't care about them anyway. That said, we do output them if they take too
			// long.
			if (logHttpRequestStatsParams.Headers["Raven-Timer-Request"] == "true" && 
				logHttpRequestStatsParams.Stopwatch.ElapsedMilliseconds <= 25)
				return;

			var curReq = Interlocked.Increment(ref reqNum);
			logger.Debug("Request #{0,4:#,0}: {1,-7} - {2,5:#,0} ms - {5,-10} - {3} - {4}",
							   curReq,
							   logHttpRequestStatsParams.HttpMethod,
							   logHttpRequestStatsParams.Stopwatch.ElapsedMilliseconds,
							   logHttpRequestStatsParams.ResponseStatusCode,
							   logHttpRequestStatsParams.RequestUri,
							   currentTenantId.Value);
		}

		private void HandleException(IHttpContext ctx, Exception e)
		{
			try
			{
				if (e is BadRequestException)
					HandleBadRequest(ctx, (BadRequestException)e);
				else if (e is ConcurrencyException)
					HandleConcurrencyException(ctx, (ConcurrencyException)e);
				else if (TryHandleException(ctx, e))
					return;
				else
					HandleGenericException(ctx, e);
			}
			catch (Exception)
			{
				logger.ErrorException("Failed to properly handle error, further error handling is ignored", e);
			}
		}

		protected bool TryHandleException(IHttpContext ctx, Exception exception)
		{
			var indexDisabledException = exception as IndexDisabledException;
			if (indexDisabledException != null)
			{
				HandleIndexDisabledException(ctx, indexDisabledException);
				return true;
			}
			var indexDoesNotExistsException = exception as IndexDoesNotExistsException;
			if (indexDoesNotExistsException != null)
			{
				HandleIndexDoesNotExistsException(ctx, indexDoesNotExistsException);
				return true;
			}

			return false;
		}

		private static void HandleIndexDoesNotExistsException(IHttpContext ctx, Exception e)
		{
			ctx.SetStatusToNotFound();
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				Error = e.Message
			});
		}


		private static void HandleIndexDisabledException(IHttpContext ctx, IndexDisabledException e)
		{
			ctx.Response.StatusCode = 503;
			ctx.Response.StatusDescription = "Service Unavailable";
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				Error = e.Information.GetErrorMessage(),
				Index = e.Information.Name,
			});
		}

		private static void HandleTooBusyError(IHttpContext ctx)
		{
			ctx.Response.StatusCode = 503;
			ctx.Response.StatusDescription = "Service Unavailable";
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				Error = "The server is too busy, could not acquire transactional access"
			});
		}


		private static void HandleGenericException(IHttpContext ctx, Exception e)
		{
			ctx.Response.StatusCode = 500;
			ctx.Response.StatusDescription = "Internal Server Error";
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				Error = e.ToString()
			});
		}

		private static void HandleBadRequest(IHttpContext ctx, BadRequestException e)
		{
			ctx.SetStatusToBadRequest();
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				e.Message,
				Error = e.Message
			});
		}

		private static void HandleConcurrencyException(IHttpContext ctx, ConcurrencyException e)
		{
			ctx.Response.StatusCode = 409;
			ctx.Response.StatusDescription = "Conflict";
			SerializeError(ctx, new
			{
				Url = ctx.Request.RawUrl,
				e.ActualETag,
				e.ExpectedETag,
				Error = e.Message
			});
		}

		protected static void SerializeError(IHttpContext ctx, object error)
		{
			var sw = new StreamWriter(ctx.Response.OutputStream);
			new JsonSerializer().Serialize(new JsonTextWriter(sw)
			{
				Formatting = Formatting.Indented,
			}, error);
			sw.Flush();
		}

		private bool DispatchRequest(IHttpContext ctx)
		{
			SetupRequestToProperDatabase(ctx);

			CurrentOperationContext.Headers.Value = ctx.Request.Headers;

			CurrentOperationContext.Headers.Value[Constants.RavenAuthenticatedUser] = "";
			if (ctx.RequiresAuthentication &&
				requestAuthorizer.Authorize(ctx) == false)
				return false;

			try
			{
				OnDispatchingRequest(ctx);

				if (DefaultConfiguration.HttpCompression)
					AddHttpCompressionIfClientCanAcceptIt(ctx);

				// Cross-Origin Resource Sharing (CORS) is documented here: http://www.w3.org/TR/cors/
				AddAccessControlHeaders(ctx);
				if (ctx.Request.HttpMethod == "OPTIONS")
					return false;

				foreach (var requestResponderLazy in RequestResponders)
				{
					var requestResponder = requestResponderLazy.Value;
					if (requestResponder.WillRespond(ctx))
					{
						requestResponder.Respond(ctx);
						return requestResponder.IsUserInterfaceRequest;
					}
				}
				ctx.SetStatusToBadRequest();
				if (ctx.Request.HttpMethod == "HEAD")
					return false;
				ctx.Write(
					@"
<html>
	<body>
		<h1>Could not figure out what to do</h1>
		<p>Your request didn't match anything that Raven knows to do, sorry...</p>
	</body>
</html>
");
			}
			finally
			{
				CurrentOperationContext.Headers.Value = new NameValueCollection();
				currentDatabase.Value = DefaultResourceStore;
				currentConfiguration.Value = DefaultConfiguration;
			}
			return false;
		}

		protected void OnDispatchingRequest(IHttpContext ctx)
		{
			ctx.Response.AddHeader("Raven-Server-Build", DocumentDatabase.BuildVersion);
		}

		private void SetupRequestToProperDatabase(IHttpContext ctx)
		{
			var requestUrl = ctx.GetRequestUrlForTenantSelection();
			var match = databaseQuery.Match(requestUrl);

			if (match.Success == false)
			{
				currentTenantId.Value = Constants.DefaultDatabase;
				currentDatabase.Value = DefaultResourceStore;
				currentConfiguration.Value = DefaultConfiguration;
			}
			else
			{
				var tenantId = match.Groups[1].Value;
				DocumentDatabase resourceStore;
				if (TryGetOrCreateResourceStore(tenantId, out resourceStore))
				{
					databaseLastRecentlyUsed.AddOrUpdate(tenantId, SystemTime.Now, (s, time) => SystemTime.Now);

					if (string.IsNullOrEmpty(Configuration.VirtualDirectory) == false && Configuration.VirtualDirectory != "/")
					{
						ctx.AdjustUrl(Configuration.VirtualDirectory + match.Value);
					}
					else
					{
						ctx.AdjustUrl(match.Value);
					}
					currentTenantId.Value = tenantId;
					currentDatabase.Value = resourceStore;
					currentConfiguration.Value = resourceStore.Configuration;
				}
				else
				{
					throw new BadRequestException("Could not find a database named: " + tenantId);
				}
			}
		}

		protected bool TryGetOrCreateResourceStore(string tenantId, out DocumentDatabase database)
		{
			if (ResourcesStoresCache.TryGetValue(tenantId, out database))
				return true;

			JsonDocument jsonDocument;

			using (DefaultResourceStore.DisableAllTriggersForCurrentThread())
				jsonDocument = DefaultResourceStore.Get("Raven/Databases/" + tenantId, null);

			if (jsonDocument == null)
				return false;

			var document = jsonDocument.DataAsJson.JsonDeserialization<DatabaseDocument>();

			database = ResourcesStoresCache.GetOrAddAtomically(tenantId, s =>
			{
				var config = new InMemoryRavenConfiguration
				{
					Settings = DefaultConfiguration.Settings,
				};
				foreach (var setting in document.Settings)
				{
					config.Settings[setting.Key] = setting.Value;
				}
				var dataDir = config.Settings["Raven/DataDir"];
				if (dataDir == null)
					throw new InvalidOperationException("Could not find Raven/DataDir");
				if (dataDir.StartsWith("~/") || dataDir.StartsWith(@"~\"))
				{
					var baseDataPath = Path.GetDirectoryName(DefaultResourceStore.Configuration.DataDirectory);
					if (baseDataPath == null)
						throw new InvalidOperationException("Could not find root data path");
					config.Settings["Raven/DataDir"] = Path.Combine(baseDataPath, dataDir.Substring(2));
				}
				config.Settings["Raven/VirtualDir"] = config.Settings["Raven/VirtualDir"] + "/" + tenantId;

				config.DatabaseName = tenantId;

				config.Initialize();
				var documentDatabase = new DocumentDatabase(config);
				documentDatabase.SpinBackgroundWorkers();
				return documentDatabase;
			});
			return true;
		}


		private void AddAccessControlHeaders(IHttpContext ctx)
		{
			if (string.IsNullOrEmpty(DefaultConfiguration.AccessControlAllowOrigin))
				return;
			ctx.Response.AddHeader("Access-Control-Allow-Origin", DefaultConfiguration.AccessControlAllowOrigin);
			ctx.Response.AddHeader("Access-Control-Max-Age", DefaultConfiguration.AccessControlMaxAge);
			ctx.Response.AddHeader("Access-Control-Allow-Methods", DefaultConfiguration.AccessControlAllowMethods);
			if (string.IsNullOrEmpty(DefaultConfiguration.AccessControlRequestHeaders))
			{
				// allow whatever headers are being requested
				var hdr = ctx.Request.Headers["Access-Control-Request-Headers"]; // typically: "x-requested-with"
				if (hdr != null) ctx.Response.AddHeader("Access-Control-Allow-Headers", hdr);
			}
			else
			{
				ctx.Response.AddHeader("Access-Control-Request-Headers", DefaultConfiguration.AccessControlRequestHeaders);
			}
		}

		private static void AddHttpCompressionIfClientCanAcceptIt(IHttpContext ctx)
		{
			var acceptEncoding = ctx.Request.Headers["Accept-Encoding"];

			if (string.IsNullOrEmpty(acceptEncoding))
				return;

			// gzip must be first, because chrome has an issue accepting deflate data
			// when sending it json text
			if ((acceptEncoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) != -1))
			{
				ctx.SetResponseFilter(s => new GZipStream(s, CompressionMode.Compress, true));
				ctx.Response.AddHeader("Content-Encoding", "gzip");
			}
			else if (acceptEncoding.IndexOf("deflate", StringComparison.OrdinalIgnoreCase) != -1)
			{
				ctx.SetResponseFilter(s => new DeflateStream(s, CompressionMode.Compress, true));
				ctx.Response.AddHeader("Content-Encoding", "deflate");
			}

		}

		public void ResetNumberOfRequests()
		{
			Interlocked.Exchange(ref reqNum, 0);
			Interlocked.Exchange(ref physicalRequestsCount, 0);
		}
	}
}
