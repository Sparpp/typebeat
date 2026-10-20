// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net.Http;
using osu.Framework.IO.Network;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Posts a top-level comment. There is no reply form: the server's comments are flat and it
    /// refuses a <c>comment[parent_id]</c> with 422, so the parent parameter lazer carried is gone.
    /// </summary>
    public class CommentPostRequest : APIRequest<CommentBundle>
    {
        public readonly CommentableType Commentable;
        public readonly long CommentableId;
        public readonly string Message;

        public CommentPostRequest(CommentableType commentable, long commentableId, string message)
        {
            Commentable = commentable;
            CommentableId = commentableId;
            Message = message;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();
            req.Method = HttpMethod.Post;

            req.AddParameter(@"comment[commentable_type]", Commentable.ToString().ToLowerInvariant());
            req.AddParameter(@"comment[commentable_id]", $"{CommentableId}");
            req.AddParameter(@"comment[message]", Message);

            return req;
        }

        protected override string Target => "comments";
    }
}
