// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Authentication
{
    /// <summary>
    /// An opinionated abstraction for implementing <see cref="IAuthenticationHandler"/>.
    /// </summary>
    /// <typeparam name="TOptions">The type for the options used to configure the authentication handler.</typeparam>
    public abstract partial class AuthenticationHandler<TOptions> : IAuthenticationHandler where TOptions : AuthenticationSchemeOptions, new()
    {
        private Task<AuthenticateResult>? _authenticateTask;

        /// <summary>
        /// Gets or sets the <see cref="AuthenticationScheme"/> asssociated with this authentication handler.
        /// </summary>
        public AuthenticationScheme Scheme { get; private set; } = default!;

        /// <summary>
        /// Gets or sets the options associated with this authentication handler.
        /// </summary>
        public TOptions Options { get; private set; } = default!;

        /// <summary>
        /// Gets or sets the <see cref="HttpContext"/>.
        /// </summary>
        protected HttpContext Context { get; private set; } = default!;

        /// <summary>
        /// Gets the <see cref="HttpRequest"/> associated with the current request.
        /// </summary>
        protected HttpRequest Request
        {
            get => Context.Request;
        }

        /// <summary>
        /// Gets the <see cref="HttpResponse" /> associated with the current request.
        /// </summary>
        protected HttpResponse Response
        {
            get => Context.Response;
        }

        /// <summary>
        /// Gets the path as seen by the authentication middleware.
        /// </summary>
        protected PathString OriginalPath => Context.Features.Get<IAuthenticationFeature>()?.OriginalPath ?? Request.Path;

        /// <summary>
        /// Gets the path base as seen by the authentication middleware.
        /// </summary>
        protected PathString OriginalPathBase => Context.Features.Get<IAuthenticationFeature>()?.OriginalPathBase ?? Request.PathBase;

        /// <summary>
        /// Gets the <see cref="ILogger"/>.
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        /// Gets the <see cref="UrlEncoder"/>.
        /// </summary>
        protected UrlEncoder UrlEncoder { get; }

        /// <summary>
        /// Gets the <see cref="ISystemClock"/>.
        /// </summary>
        protected ISystemClock Clock { get; }

        /// <summary>
        /// Gets the <see cref="IOptionsMonitor{TOptions}"/> to detect changes to options.
        /// </summary>
        protected IOptionsMonitor<TOptions> OptionsMonitor { get; }

        /// <summary>
        /// The handler calls methods on the events which give the application control at certain points where processing is occurring.
        /// If it is not provided a default instance is supplied which does nothing when the methods are called.
        /// </summary>
        protected virtual object? Events { get; set; }

        /// <summary>
        /// Gets the issuer that should be used when any claims are issued.
        /// </summary>
        /// <value>
        /// The <c>ClaimsIssuer</c> configured in <typeparamref name="TOptions"/>, if configured, otherwise <see cref="AuthenticationScheme.Name"/>.
        /// </value>
        protected virtual string ClaimsIssuer => Options.ClaimsIssuer ?? Scheme.Name;

        /// <summary>
        /// Gets the absolute current url.
        /// </summary>
        protected string CurrentUri
        {
            get => Request.Scheme + Uri.SchemeDelimiter + Request.Host + Request.PathBase + Request.Path + Request.QueryString;
        }

        /// <summary>
        /// Initializes a new instance of <see cref="AuthenticationHandler{TOptions}"/>.
        /// </summary>
        /// <param name="options">The monitor for the options instance.</param>
        /// <param name="logger">The <see cref="ILoggerFactory"/>.</param>
        /// <param name="encoder">The <see cref="System.Text.Encodings.Web.UrlEncoder"/>.</param>
        /// <param name="clock">The <see cref="ISystemClock"/>.</param>
        protected AuthenticationHandler(IOptionsMonitor<TOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
        {
            Logger = logger.CreateLogger(this.GetType().FullName!);
            UrlEncoder = encoder;
            Clock = clock;
            OptionsMonitor = options;
        }

        /// <summary>
        /// Initialize the handler, resolve the options and validate them.
        /// </summary>
        /// <param name="scheme"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            if (scheme == null)
            {
                throw new ArgumentNullException(nameof(scheme));
            }
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            Scheme = scheme;
            Context = context;

            Options = OptionsMonitor.Get(Scheme.Name);

            await InitializeEventsAsync();
            await InitializeHandlerAsync();
        }

        /// <summary>
        /// Initializes the events object, called once per request by <see cref="InitializeAsync(AuthenticationScheme, HttpContext)"/>.
        /// </summary>
        protected virtual async Task InitializeEventsAsync()
        {
            Events = Options.Events;
            if (Options.EventsType != null)
            {
                Events = Context.RequestServices.GetRequiredService(Options.EventsType);
            }
            Events ??= await CreateEventsAsync();
        }

        /// <summary>
        /// Creates a new instance of the events instance.
        /// </summary>
        /// <returns>A new instance of the events instance.</returns>
        protected virtual Task<object> CreateEventsAsync() => Task.FromResult(new object());

        /// <summary>
        /// Called after options/events have been initialized for the handler to finish initializing itself.
        /// </summary>
        /// <returns>A task</returns>
        protected virtual Task InitializeHandlerAsync() => Task.CompletedTask;

        /// <summary>
        /// Constructs an absolute url for the specified <paramref name="targetPath"/>.
        /// </summary>
        /// <param name="targetPath">The path.</param>
        /// <returns>The absolute url.</returns>
        protected string BuildRedirectUri(string targetPath)
            => Request.Scheme + Uri.SchemeDelimiter + Request.Host + OriginalPathBase + targetPath;

        /// <summary>
        /// Resolves the scheme that this authentication operation is forwarded to.
        /// </summary>
        /// <param name="scheme">The scheme to forward. One of ForwardAuthenticate, ForwardChallenge, ForwardForbid, ForwardSignIn, or ForwardSignOut.</param>
        /// <returns>The forwarded scheme or <see langword="null"/>.</returns>
        protected virtual string? ResolveTarget(string? scheme)
        {
            var target = scheme ?? Options.ForwardDefaultSelector?.Invoke(Context) ?? Options.ForwardDefault;

            // Prevent self targetting
            return string.Equals(target, Scheme.Name, StringComparison.Ordinal)
                ? null
                : target;
        }

        /// <inheritdoc />
        public async Task<AuthenticateResult> AuthenticateAsync()
        {
            var target = ResolveTarget(Options.ForwardAuthenticate);
            if (target != null)
            {
                return await Context.AuthenticateAsync(target);
            }

            // Calling Authenticate more than once should always return the original value.
            var result = await HandleAuthenticateOnceAsync() ?? AuthenticateResult.NoResult();
            if (result.Failure == null)
            {
                var ticket = result.Ticket;
                if (ticket?.Principal != null)
                {
                    Log.AuthenticationSchemeAuthenticated(Logger, Scheme.Name);
                }
                else
                {
                    Log.AuthenticationSchemeNotAuthenticated(Logger, Scheme.Name);
                }
            }
            else
            {
                Log.AuthenticationSchemeNotAuthenticatedWithFailure(Logger, Scheme.Name, result.Failure.Message);
            }
            return result;
        }

        /// <summary>
        /// Used to ensure HandleAuthenticateAsync is only invoked once. The subsequent calls
        /// will return the same authenticate result.
        /// </summary>
        protected Task<AuthenticateResult> HandleAuthenticateOnceAsync()
        {
            if (_authenticateTask == null)
            {
                _authenticateTask = HandleAuthenticateAsync();
            }

            return _authenticateTask;
        }

        /// <summary>
        /// Used to ensure HandleAuthenticateAsync is only invoked once safely. The subsequent
        /// calls will return the same authentication result. Any exceptions will be converted
        /// into a failed authentication result containing the exception.
        /// </summary>
        protected async Task<AuthenticateResult> HandleAuthenticateOnceSafeAsync()
        {
            try
            {
                return await HandleAuthenticateOnceAsync();
            }
            catch (Exception ex)
            {
                return AuthenticateResult.Fail(ex);
            }
        }

        /// <summary>
        /// Allows derived types to handle authentication.
        /// </summary>
        /// <returns>The <see cref="AuthenticateResult"/>.</returns>
        protected abstract Task<AuthenticateResult> HandleAuthenticateAsync();

        /// <summary>
        /// Override this method to handle Forbid.
        /// </summary>
        /// <param name="properties"></param>
        /// <returns>A Task.</returns>
        protected virtual Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 403;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Override this method to deal with 401 challenge concerns, if an authentication scheme in question
        /// deals an authentication interaction as part of it's request flow. (like adding a response header, or
        /// changing the 401 result to 302 of a login page or external sign-in location.)
        /// </summary>
        /// <param name="properties"></param>
        /// <returns>A Task.</returns>
        protected virtual Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 401;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task ChallengeAsync(AuthenticationProperties? properties)
        {
            var target = ResolveTarget(Options.ForwardChallenge);
            if (target != null)
            {
                await Context.ChallengeAsync(target, properties);
                return;
            }

            properties ??= new AuthenticationProperties();
            await HandleChallengeAsync(properties);
            Log.AuthenticationSchemeChallenged(Logger, Scheme.Name);
        }

        /// <inheritdoc />
        public async Task ForbidAsync(AuthenticationProperties? properties)
        {
            var target = ResolveTarget(Options.ForwardForbid);
            if (target != null)
            {
                await Context.ForbidAsync(target, properties);
                return;
            }

            properties ??= new AuthenticationProperties();
            await HandleForbiddenAsync(properties);
            Log.AuthenticationSchemeForbidden(Logger, Scheme.Name);
        }

        private partial class Log
        {
            [LoggerMessage(7, LogLevel.Information, "{AuthenticationScheme} was not authenticated. Failure message: {FailureMessage}", EventName = "AuthenticationSchemeNotAuthenticatedWithFailure")]
            public static partial void AuthenticationSchemeNotAuthenticatedWithFailure(ILogger logger, string authenticationScheme, string failureMessage);

            [LoggerMessage(8, LogLevel.Debug, "AuthenticationScheme: {AuthenticationScheme} was successfully authenticated.", EventName = "AuthenticationSchemeAuthenticated")]
            public static partial void AuthenticationSchemeAuthenticated(ILogger logger, string authenticationScheme);

            [LoggerMessage(9, LogLevel.Debug, "AuthenticationScheme: {AuthenticationScheme} was not authenticated.", EventName = "AuthenticationSchemeNotAuthenticated")]
            public static partial void AuthenticationSchemeNotAuthenticated(ILogger logger, string authenticationScheme);

            [LoggerMessage(12, LogLevel.Information, "AuthenticationScheme: {AuthenticationScheme} was challenged.", EventName = "AuthenticationSchemeChallenged")]
            public static partial void AuthenticationSchemeChallenged(ILogger logger, string authenticationScheme);

            [LoggerMessage(13, LogLevel.Information, "AuthenticationScheme: {AuthenticationScheme} was forbidden.", EventName = "AuthenticationSchemeForbidden")]
            public static partial void AuthenticationSchemeForbidden(ILogger logger, string authenticationScheme);
        }
    }
}
