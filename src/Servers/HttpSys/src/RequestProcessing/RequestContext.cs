// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpSys.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.HttpSys;

internal partial class RequestContext : NativeRequestContext, IThreadPoolWorkItem
{
    private static readonly Action<object?> AbortDelegate = Abort;
    private CancellationTokenSource? _requestAbortSource;
    private CancellationToken? _disconnectToken;
    private bool _disposed;
    private bool _initialized;

    public RequestContext(HttpSysListener server, uint? bufferSize, ulong requestId)
        : base(server.MemoryPool, bufferSize, requestId, server.Options.UseLatin1RequestHeaders)
    {
        Server = server;
        AllowSynchronousIO = server.Options.AllowSynchronousIO;
    }

    internal HttpSysListener Server { get; }

    internal ILogger Logger => Server.Logger;

    public Request Request { get; private set; } = default!;

    public Response Response { get; private set; } = default!;

    public WindowsPrincipal User => Request.User;

    public CancellationToken DisconnectToken
    {
        get
        {
            // Create a new token per request, but link it to a single connection token.
            // We need to be able to dispose of the registrations each request to prevent leaks.
            if (!_disconnectToken.HasValue)
            {
                if (_disposed || Response.BodyIsFinished)
                {
                    // We cannot register for disconnect notifications after the response has finished sending.
                    _disconnectToken = CancellationToken.None;
                }
                else
                {
                    var connectionDisconnectToken = Server.DisconnectListener.GetTokenForConnection(Request.UConnectionId);

                    if (connectionDisconnectToken.CanBeCanceled)
                    {
                        _requestAbortSource = CancellationTokenSource.CreateLinkedTokenSource(connectionDisconnectToken);
                        _disconnectToken = _requestAbortSource.Token;
                    }
                    else
                    {
                        _disconnectToken = CancellationToken.None;
                    }
                }
            }
            return _disconnectToken.Value;
        }
    }

    public unsafe Guid TraceIdentifier
    {
        get
        {
            // This is the base GUID used by HTTP.SYS for generating the activity ID.
            // HTTP.SYS overwrites the first 8 bytes of the base GUID with RequestId to generate ETW activity ID.
            var guid = new Guid(0xffcb4c93, 0xa57f, 0x453c, 0xb6, 0x3f, 0x84, 0x71, 0xc, 0x79, 0x67, 0xbb);
            *((ulong*)&guid) = Request.RequestId;
            return guid;
        }
    }

    public bool IsUpgradableRequest => Request.IsUpgradable;

    internal bool AllowSynchronousIO { get; set; }

    public Task<Stream> UpgradeAsync()
    {
        if (!IsUpgradableRequest)
        {
            throw new InvalidOperationException("This request cannot be upgraded, it is incompatible.");
        }
        if (Response.HasStarted)
        {
            throw new InvalidOperationException("This request cannot be upgraded, the response has already started.");
        }

        // Set the status code and reason phrase
        Response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        Response.ReasonPhrase = HttpReasonPhrase.Get(StatusCodes.Status101SwitchingProtocols);

        Response.SendOpaqueUpgrade(); // TODO: Async
        Request.SwitchToOpaqueMode();
        Response.SwitchToOpaqueMode();
        var opaqueStream = new OpaqueStream(Request.Body, Response.Body);
        return Task.FromResult<Stream>(opaqueStream);
    }

    // TODO: Public when needed
    internal bool TryGetChannelBinding(ref ChannelBinding? value)
    {
        if (!Request.IsHttps)
        {
            Log.ChannelBindingNeedsHttps(Logger);
            return false;
        }

        value = ClientCertLoader.GetChannelBindingFromTls(Server.RequestQueue, Request.UConnectionId, Logger);

        Debug.Assert(value != null, "GetChannelBindingFromTls returned null even though OS supposedly supports Extended Protection");
        Log.ChannelBindingRetrieved(Logger);
        return value != null;
    }

    /// <summary>
    /// Flushes and completes the response.
    /// </summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_initialized)
        {
            // TODO: Verbose log
            try
            {
                _requestAbortSource?.Dispose();
                Response.Dispose();
            }
            catch
            {
                Abort();
            }
            finally
            {
                Request.Dispose();
            }
        }

        base.Dispose();
    }

    /// <summary>
    /// Forcibly terminate and dispose the request, closing the connection if necessary.
    /// </summary>
    public void Abort()
    {
        // May be called from Dispose() code path, don't check _disposed.
        // TODO: Verbose log
        _disposed = true;
        if (_requestAbortSource != null)
        {
            try
            {
                _requestAbortSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.AbortError(Logger, ex);
            }
            _requestAbortSource.Dispose();
        }
        else
        {
            _disconnectToken = new CancellationToken(canceled: true);
        }
        ForceCancelRequest();
        Request.Dispose();
        // Only Abort, Response.Dispose() tries a graceful flush
        Response.Abort();
    }

    private static void Abort(object? state)
    {
        var context = (RequestContext)state!;
        context.Abort();
    }

    internal CancellationTokenRegistration RegisterForCancellation(CancellationToken cancellationToken)
    {
        return cancellationToken.Register(AbortDelegate, this);
    }

    // The request is being aborted, but large writes may be in progress. Cancel them.
    internal void ForceCancelRequest()
    {
        try
        {
            var statusCode = HttpApi.HttpCancelHttpRequest(Server.RequestQueue.Handle,
                Request.RequestId, IntPtr.Zero);

            // Either the connection has already dropped, or the last write is in progress.
            // The requestId becomes invalid as soon as the last Content-Length write starts.
            // The only way to cancel now is with CancelIoEx.
            if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_CONNECTION_INVALID)
            {
                Response.CancelLastWrite();
            }
        }
        catch (ObjectDisposedException)
        {
            // RequestQueueHandle may have been closed
        }
    }

    // You must still call ForceCancelRequest after this.
    internal unsafe void SetResetCode(int errorCode)
    {
        if (!HttpApi.SupportsReset)
        {
            return;
        }

        try
        {
            var streamError = new HttpApiTypes.HTTP_REQUEST_PROPERTY_STREAM_ERROR() { ErrorCode = (uint)errorCode };
            var statusCode = HttpApi.HttpSetRequestProperty(Server.RequestQueue.Handle, Request.RequestId, HttpApiTypes.HTTP_REQUEST_PROPERTY.HttpRequestPropertyStreamError, (void*)&streamError,
                (uint)sizeof(HttpApiTypes.HTTP_REQUEST_PROPERTY_STREAM_ERROR), IntPtr.Zero);
        }
        catch (ObjectDisposedException)
        {
            // RequestQueueHandle may have been closed
        }
    }

    public virtual Task ExecuteAsync()
    {
        return Task.CompletedTask;
    }

    public void Execute()
    {
        _ = ExecuteAsync();
    }

    protected void SetFatalResponse(int status)
    {
        Response.StatusCode = status;
        Response.ContentLength = 0;
        Dispose();
    }

    internal unsafe void Delegate(DelegationRule destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }
        if (Request.HasRequestBodyStarted)
        {
            throw new InvalidOperationException("This request cannot be delegated, the request body has already started.");
        }
        if (Response.HasStarted)
        {
            throw new InvalidOperationException("This request cannot be delegated, the response has already started.");
        }

        var source = Server.RequestQueue;

        uint statusCode;

        fixed (char* uriPointer = destination.UrlPrefix)
        {
            var property = new HttpApiTypes.HTTP_DELEGATE_REQUEST_PROPERTY_INFO()
            {
                PropertyId = HttpApiTypes.HTTP_DELEGATE_REQUEST_PROPERTY_ID.DelegateRequestDelegateUrlProperty,
                PropertyInfo = (IntPtr)uriPointer,
                PropertyInfoLength = (uint)System.Text.Encoding.Unicode.GetByteCount(destination.UrlPrefix)
            };

            statusCode = HttpApi.HttpDelegateRequestEx(source.Handle,
                                                           destination.Queue.Handle,
                                                           Request.RequestId,
                                                           destination.Queue.UrlGroup.Id,
                                                           propertyInfoSetSize: 1,
                                                           &property);
        }

        if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
        {
            throw new HttpSysException((int)statusCode);
        }

        Response.MarkDelegated();
    }
}
