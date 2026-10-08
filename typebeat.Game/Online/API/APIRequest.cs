// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.IO.Network;
using osu.Framework.Logging;
using typebeat.Game.Extensions;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API
{
    /// <summary>
    /// An API request with a well-defined response type.
    /// </summary>
    /// <typeparam name="T">Type of the response (used for deserialisation).</typeparam>
    public abstract class APIRequest<T> : APIRequest where T : class
    {
        protected override WebRequest CreateWebRequest() => new OsuJsonWebRequest<T>(Uri);

        /// <summary>
        /// The deserialised response object. May be null if the request or deserialisation failed.
        /// </summary>
        public T? Response { get; private set; }

        /// <summary>
        /// Invoked on successful completion of an API request.
        /// This will be scheduled to the API's internal scheduler (run on update thread automatically).
        /// </summary>
        public new event APISuccessHandler<T>? Success;

        protected APIRequest()
        {
            base.Success += () => Success?.Invoke(Response!);
        }

        protected override void PostProcess()
        {
            base.PostProcess();

            if (WebRequest != null)
            {
                Response = ((OsuJsonWebRequest<T>)WebRequest).ResponseObject;
                Logger.Log($"{GetType().ReadableName()} finished with response size of {WebRequest.ResponseStream.Length:#,0} bytes", LoggingTarget.Network);
            }

            if (Response == null)
                TriggerFailure(new ArgumentNullException(nameof(Response)));
        }

        internal void TriggerSuccess(T result)
        {
            if (Response != null)
                throw new InvalidOperationException("Attempted to trigger success more than once");

            Response = result;

            TriggerSuccess();
        }
    }

    /// <summary>
    /// AN API request with no specified response type.
    /// </summary>
    public abstract class APIRequest
    {
        protected abstract string Target { get; }

        protected virtual WebRequest CreateWebRequest() => new OsuWebRequest(Uri);

        protected virtual string Uri => $@"{API!.Endpoints.APIUrl}/api/v2/{Target}";

        protected IAPIProvider? API;

        protected WebRequest? WebRequest;

        /// <summary>
        /// Whether this request may be sent with no login at all, so that a guest can perform it.
        /// </summary>
        /// <remarks>
        /// Opt-in, and only for READS of data the website already shows to anyone: map lookups, set
        /// listings, the global leaderboard, public profiles, comments, downloads. <see cref="APIAccess"/>
        /// queues and sends such a request while <see cref="APIState.Offline"/> (no Authorization header is
        /// attached, as there is no token), and still fails every other request on the spot with
        /// "User not logged in". Anything that writes, or reads the signed-in user's own state (score
        /// tokens and submission, favourites, friends, chat, comments posting, <c>/me</c>), must leave
        /// this false: the server refuses those anonymously, and a refusal here is a fast local failure
        /// rather than a round trip.
        /// <para>
        /// Never serialised: some requests are their own JSON body (see <c>PutBeatmapSetRequest</c>), and this is
        /// client-side routing, not something to put on the wire.
        /// </para>
        /// </remarks>
        [JsonIgnore]
        public virtual bool AllowsAnonymous => false;

        /// <summary>
        /// The exception this request failed with, or null while it has not failed.
        /// </summary>
        /// <remarks>
        /// Set at the same moment <see cref="CompletionState"/> becomes <see cref="APIRequestCompletionState.Failed"/>,
        /// so a caller that performed the request synchronously can tell WHY it failed without waiting on the
        /// <see cref="Failure"/> callback, which runs later on the update thread.
        /// </remarks>
        [JsonIgnore]
        public Exception? FailureException { get; private set; }

        /// <summary>
        /// The currently logged in user. Note that this will only be populated during <see cref="Perform"/>.
        /// </summary>
        protected APIUser? User { get; private set; }

        /// <summary>
        /// Invoked on successful completion of an API request.
        /// This will be scheduled to the API's internal scheduler (run on update thread automatically).
        /// </summary>
        public event APISuccessHandler? Success;

        /// <summary>
        /// Invoked on failure to complete an API request.
        /// This will be scheduled to the API's internal scheduler (run on update thread automatically).
        /// </summary>
        public event APIFailureHandler? Failure;

        private readonly object completionStateLock = new object();

        /// <summary>
        /// The state of this request, from an outside perspective.
        /// This is used to ensure correct notification events are fired.
        /// </summary>
        public APIRequestCompletionState CompletionState { get; private set; }

        /// <summary>
        /// Should be called before <see cref="Perform"/> to give API context.
        /// </summary>
        /// <remarks>
        /// This allows scheduling of operations back to the correct thread (which may be required before <see cref="Perform"/> is called).
        /// </remarks>
        public void AttachAPI(IAPIProvider apiAccess)
        {
            if (API != null && API != apiAccess)
                throw new InvalidOperationException("Attached API cannot be changed after initial set.");

            API = apiAccess;
        }

        public void Perform()
        {
            if (API == null)
            {
                Fail(new NotSupportedException($"A {nameof(APIAccess)} is required to perform requests."));
                return;
            }

            User = API.LocalUser.Value;

            if (isFailing) return;

            WebRequest = CreateWebRequest();
            WebRequest.Failed += Fail;
            WebRequest.AllowRetryOnTimeout = false;

            WebRequest.AddHeader(@"Accept-Language", API.Language.ToCultureCode());
            WebRequest.AddHeader(@"x-api-version", API.APIVersion.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(API.AccessToken))
                WebRequest.AddHeader(@"Authorization", $@"Bearer {API.AccessToken}");

            if (isFailing) return;

            try
            {
                Logger.Log($@"Performing request {this}", LoggingTarget.Network);
                WebRequest.Perform();
            }
            catch (OperationCanceledException)
            {
                // ignore this. internally Perform is running async and the fail state may have changed since
                // the last check of `isFailing` above.
            }

            if (isFailing) return;

            PostProcess();

            if (isFailing) return;

            TriggerSuccess();
        }

        /// <summary>
        /// Perform any post-processing actions after a successful request.
        /// </summary>
        protected virtual void PostProcess()
        {
        }

        internal void TriggerSuccess()
        {
            Debug.Assert(API != null);

            lock (completionStateLock)
            {
                if (CompletionState != APIRequestCompletionState.Waiting)
                    return;

                CompletionState = APIRequestCompletionState.Completed;
            }

            API.Schedule(() => Success?.Invoke());
        }

        internal void TriggerFailure(Exception e)
        {
            Debug.Assert(API != null);

            lock (completionStateLock)
            {
                if (CompletionState != APIRequestCompletionState.Waiting)
                    return;

                CompletionState = APIRequestCompletionState.Failed;
                FailureException = e;
            }

            API.Schedule(() => Failure?.Invoke(e));
        }

        public void Cancel() => Fail(new OperationCanceledException(@"Request cancelled"));

        public void Fail(Exception e)
        {
            lock (completionStateLock)
            {
                if (CompletionState != APIRequestCompletionState.Waiting)
                    return;

                WebRequest?.Abort();

                // in the case of a cancellation we don't care about whether there's an error in the response.
                if (!(e is OperationCanceledException))
                {
                    string? responseString = WebRequest?.GetResponseString();

                    // naive check whether there's an error in the response to avoid unnecessary JSON deserialisation.
                    if (!string.IsNullOrEmpty(responseString) && responseString.Contains(@"""error"""))
                    {
                        try
                        {
                            // attempt to decode a displayable error string.
                            var error = JsonConvert.DeserializeObject<DisplayableError>(responseString);
                            if (error != null)
                                e = new APIException(error.ErrorMessage, e, WebRequest?.ResponseStatusCode);
                        }
                        catch
                        {
                        }
                    }
                }

                Logger.Log($@"Failing request {this} ({e})", LoggingTarget.Network);
                TriggerFailure(e);
            }
        }

        /// <summary>
        /// Whether this request is in a failing or failed state.
        /// </summary>
        private bool isFailing
        {
            get
            {
                lock (completionStateLock)
                    return CompletionState == APIRequestCompletionState.Failed;
            }
        }

        private class DisplayableError
        {
            [JsonProperty("error")]
            public string ErrorMessage { get; set; } = string.Empty;
        }
    }

    public delegate void APIFailureHandler(Exception e);

    public delegate void APISuccessHandler();

    public delegate void APIProgressHandler(long current, long total);

    public delegate void APISuccessHandler<in T>(T content);
}
