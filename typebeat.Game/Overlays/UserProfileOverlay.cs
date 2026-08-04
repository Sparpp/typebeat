// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using typebeat.Game.Extensions;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Placeholders;
using typebeat.Game.Overlays.Profile;
using typebeat.Game.Overlays.Profile.Sections;
using typebeat.Game.Rulesets;
using typebeat.Game.Users;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Overlays
{
    public partial class UserProfileOverlay : FullscreenOverlay<ProfileHeader>
    {
        protected override Container<Drawable> Content => onlineViewContainer;

        private readonly OnlineViewContainer onlineViewContainer;
        private readonly LoadingLayer loadingLayer;

        private ProfileSection? lastSection;
        private ProfileSection[]? sections;
        private GetUserRequest? userReq;
        private ProfileSectionsContainer? sectionsContainer;
        private ProfileSectionTabControl? tabs;

        private IUser? user;
        private IRulesetInfo? ruleset;

        private readonly IBindable<APIState> apiState = new Bindable<APIState>();

        [Resolved]
        private RulesetStore rulesets { get; set; } = null!;

        public UserProfileOverlay()
            : base(OverlayColourScheme.Pink)
        {
            base.Content.Add(new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    onlineViewContainer = new OnlineViewContainer($"Sign in to view the {Header.Title.Title}")
                    {
                        RelativeSizeAxes = Axes.Both
                    },
                    loadingLayer = new LoadingLayer(true)
                }
            });
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            apiState.BindTo(API.State);
            apiState.BindValueChanged(state => Schedule(() =>
            {
                if (state.NewValue == APIState.Online && user != null)
                    Scheduler.AddOnce(fetchAndSetContent);
            }));
        }

        protected override ProfileHeader CreateHeader() => new ProfileHeader();

        protected override Color4 BackgroundColour => ColourProvider.Background5;

        public void ShowUser(IUser userToShow, IRulesetInfo? userRuleset = null)
        {
            if (userToShow.OnlineID == APIUser.SYSTEM_USER_ID)
                return;

            user = userToShow;
            ruleset = userRuleset;

            Show();
            Scheduler.AddOnce(fetchAndSetContent);
        }

        private void fetchAndSetContent()
        {
            Debug.Assert(user != null);

            bool sameUser = user.OnlineID == Header.User.Value?.User.Id;
            if (sameUser && ruleset?.MatchesOnlineID(Header.User.Value?.Ruleset) == true)
                return;

            detachHeaderFromContent();

            userReq?.Cancel();

            // the field only ever holds a request whose outcome is still wanted. Dropping the reference
            // the instant it is cancelled is what lets the callbacks below discriminate: a cancelled
            // request DOES reach its Failure arm in this framework (Cancel() is Fail(OperationCanceled),
            // which runs the same TriggerFailure path as a 404), so without this a re-entry would paint
            // "couldn't load this profile" over the perfectly healthy fetch that replaced it.
            userReq = null;

            lastSection = null;

            sections = !user.IsBot
                ? createSections()
                // a bot has no profile content to show in the first place. This is a DIFFERENT emptiness from the one
                // createSections() currently returns, and the two will diverge again the moment a section is revived.
                : Array.Empty<ProfileSection>();

            if (!sameUser)
                changeOverlayColours(OverlayColourScheme.Pink.GetHue());

            recreateBaseContent();

            if (API.State.Value == APIState.Offline)
            {
                // OnlineViewContainer takes over the surface with its sign-in placeholder, and no request is
                // going to resolve the spinner, so it must not be left up over the top of that placeholder.
                loadingLayer.Hide();
                return;
            }

            var req = user.OnlineID > 1 ? new GetUserRequest(user.OnlineID, ruleset) : new GetUserRequest(user.Username, ruleset);
            var reqRuleset = ruleset;

            req.Success += u =>
            {
                if (userReq != req) return;

                userReq = null;
                userLoadComplete(u, reqRuleset);
            };

            req.Failure += e =>
            {
                if (userReq != req) return;

                userReq = null;
                userLoadFailed(e);
            };

            userReq = req;

            API.Queue(req);
            loadingLayer.Show();
        }

        private void userLoadComplete(APIUser loadedUser, IRulesetInfo? userRuleset)
        {
            Debug.Assert(sections != null && sectionsContainer != null && tabs != null);

            // reuse header and content if same colour scheme, otherwise recreate both.
            int profileHue = loadedUser.ProfileHue ?? OverlayColourScheme.Pink.GetHue();

            if (changeOverlayColours(profileHue))
                recreateBaseContent();

            var actualRuleset = rulesets.GetRuleset(userRuleset?.ShortName ?? loadedUser.PlayMode).AsNonNull();

            var userProfile = new UserProfileData(loadedUser, actualRuleset);
            Header.User.Value = userProfile;

            foreach (var sec in sectionsInDisplayOrder(loadedUser.ProfileOrder))
            {
                sec.User.Value = userProfile;

                sectionsContainer.Add(sec);
                tabs.AddItem(sec);
            }

            loadingLayer.Hide();
        }

        /// <summary>
        /// The profile sections this client is currently willing to build, in declared display order.
        /// </summary>
        /// <remarks>
        /// THIS IS DELIBERATELY EMPTY. Every section below is switched off, and the list is the whole record of why.
        /// It is not the same emptiness as the bot case in <see cref="fetchAndSetContent"/>, which is about the user;
        /// this is about the server, and the two will diverge again as soon as one of these comes back.
        ///
        /// <para>
        /// FIRST BLOCKER, THE ENDPOINTS. Each section drives <see cref="PaginatedProfileSubsection{TModel}"/> fetches
        /// against a route this server does not have: its table carries only <c>/api/v2/users/{lookup}</c> and
        /// <c>/api/v2/users/{lookup}/{ruleset}</c>. Note that the two-segment ones (<c>kudosu</c>,
        /// <c>recent_activity</c>) do not even 404 cleanly, they MIS-ROUTE into the second of those and come back as a
        /// user object that cannot deserialise into a list. Switched on today, all five render as a heading over an
        /// empty body.
        /// </para>
        ///
        /// <para>
        /// SECOND BLOCKER, THE COUNTERS, and the trap for whoever revives these: serving the endpoint is necessary but
        /// NOT sufficient. <c>PaginatedProfileSubsection.GetCount</c> reads <see cref="APIUser"/> fields
        /// (<c>ScoresBestCount</c>, <c>BeatmapPlayCountsCount</c>, and friends) that this wire never populates, so a
        /// section turned on the day its endpoint lands would still print a confident <c>0</c> beside every subsection
        /// heading of a player who has plenty. The user payload has to grow the matching count fields in the same
        /// breath. Showing a zero for something merely unknown is exactly what tasks 74, 75 and 80 removed elsewhere,
        /// and is why these are off rather than shipped half-fed.
        /// </para>
        ///
        /// <para>
        /// TO REVIVE ONE: uncomment its line, once BOTH blockers are cleared for that section. Nothing else in this
        /// file has to change. <see cref="sectionsInDisplayOrder"/> already resolves the ordering, and the tab control
        /// and the sections container are both built from whatever this returns.
        /// </para>
        ///
        /// <para>
        /// <see cref="AboutSection"/> and <see cref="MedalsSection"/> are a THIRD, unrelated case: they were never
        /// wired up upstream and are left exactly as they were found. Do not fold them in with the five above.
        /// </para>
        /// </remarks>
        private static ProfileSection[] createSections()
        {
            var enabled = new List<ProfileSection>();

            // Upstream, never implemented. Not blocked on this server:
            //   enabled.Add(new AboutSection());
            //   enabled.Add(new MedalsSection());

            // Blocked on the server. Each line names the route that has to exist (plus the count fields, see above):
            //   enabled.Add(new RecentSection());      GET /api/v2/users/{id}/recent_activity
            //   enabled.Add(new RanksSection());       GET /api/v2/users/{id}/scores/{pinned,best,firsts}
            //   enabled.Add(new HistoricalSection());  GET /api/v2/users/{id}/beatmapsets/most_played
            //                                          GET /api/v2/users/{id}/scores/recent
            //                                          (its two graph subsections additionally read
            //                                           monthly_playcounts / replays_watched_counts off the user payload)
            //   enabled.Add(new BeatmapsSection());    GET /api/v2/users/{id}/beatmapsets/{favourite,ranked,loved,guest,pending,graveyard,nominated}
            //   enabled.Add(new KudosuSection());      GET /api/v2/users/{id}/kudosu

            return enabled.ToArray();
        }

        /// <summary>
        /// The sections to render, in the order to render them, given whatever the server had to say about it.
        /// </summary>
        /// <remarks>
        /// <c>profile_order</c> is the profile owner's own curation of their section order, and osu-web always sends
        /// the complete list. This server does not send the key at all (nothing in the user payload emits it; the
        /// website's stored order is a website-side feature and is not on this wire), so <paramref name="profileOrder"/>
        /// being null is the ORDINARY case here, not an edge case. Reading it as "then show no sections" is how the
        /// success path came to resolve into a header over an empty body.
        ///
        /// <para>
        /// The rule is therefore: an order we can actually honour wins, anything else falls back to the declared order.
        /// That covers null, an empty array, and an array naming only identifiers this client does not have, all three
        /// of which mean "no usable order was stated" rather than "this profile has no sections". Sections the server
        /// omits from a usable order are still omitted, which is the osu-web behaviour and the whole point of the key.
        /// </para>
        ///
        /// <para>
        /// This resolves to nothing at present, because <see cref="createSections"/> is deliberately empty and there is
        /// nothing to order. That is the point of keeping this correct rather than deleting it: reviving a section is
        /// then one uncommented line there, with no ordering logic to rediscover or rebuild here.
        /// </para>
        /// </remarks>
        private IEnumerable<ProfileSection> sectionsInDisplayOrder(string[]? profileOrder)
        {
            Debug.Assert(sections != null);

            if (profileOrder == null)
                return sections;

            var ordered = profileOrder
                          .Select(id => sections.FirstOrDefault(s => s.Identifier == id))
                          .Where(s => s != null)
                          .Select(s => s!)
                          .ToArray();

            return ordered.Length > 0 ? ordered : sections;
        }

        /// <summary>
        /// Resolves a profile fetch that will never arrive into a stated outcome with a way out, rather than leaving
        /// the loading layer up forever over content that was already thrown away.
        /// </summary>
        private void userLoadFailed(Exception exception)
        {
            Debug.Assert(user != null);

            // the network target rather than Logger.Error: a dropped connection is not a bug report, and an error
            // would pop a "something went wrong" notification on top of a surface that is already saying so.
            Logger.Log($@"Failed to load the profile for {user.Username}: {exception}", LoggingTarget.Network);

            // nothing of the previous profile is worth keeping: recreateBaseContent() already discarded its body, and
            // the header still holds the LAST user that loaded successfully. Clearing it stops that stale identity
            // being presented as the profile that was asked for, and stops the sameUser short-circuit at the top of
            // fetchAndSetContent treating the profile that failed as already on screen when the retry comes in.
            Header.User.Value = null;

            detachHeaderFromContent();

            // the content below replaces (and therefore disposes) the sections container these point at. Null them so
            // the next fetchAndSetContent does not reach into a disposed drawable, and so userLoadComplete's assert
            // stays a real invariant rather than something that happens to hold.
            sectionsContainer = null;
            tabs = null;
            lastSection = null;
            sections = null;

            Child = new ClickablePlaceholder(UserProfileOverlayStrings.CouldNotLoadProfile, FontAwesome.Solid.Sync)
            {
                Action = () => Scheduler.AddOnce(fetchAndSetContent)
            };

            loadingLayer.Hide();
        }

        /// <summary>
        /// Lifts <see cref="FullscreenOverlay{T}.Header"/> out of the current sections container without disposing it,
        /// so that whatever replaces that container cannot take the (reused) header down with it.
        /// </summary>
        private void detachHeaderFromContent()
        {
            if (sectionsContainer != null)
                sectionsContainer.ExpandableHeader = null;
        }

        private void recreateBaseContent()
        {
            Child = new OsuContextMenuContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = sectionsContainer = new ProfileSectionsContainer
                {
                    ExpandableHeader = Header,
                    FixedHeader = tabs = new ProfileSectionTabControl
                    {
                        RelativeSizeAxes = Axes.X,
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                    },
                    HeaderBackground = new Box
                    {
                        // this is only visible as the ProfileTabControl background
                        Colour = ColourProvider.Background5,
                        RelativeSizeAxes = Axes.Both
                    },
                }
            };

            sectionsContainer.SelectedSection.ValueChanged += section =>
            {
                if (lastSection != section.NewValue)
                {
                    lastSection = section.NewValue;
                    tabs.Current.Value = lastSection!;
                }
            };

            tabs.Current.ValueChanged += section =>
            {
                if (lastSection == null)
                {
                    lastSection = sectionsContainer.Children.FirstOrDefault();
                    if (lastSection != null)
                        tabs.Current.Value = lastSection;
                    return;
                }

                if (lastSection != section.NewValue)
                {
                    lastSection = section.NewValue;
                    sectionsContainer.ScrollTo(lastSection);
                }
            };
        }

        private bool changeOverlayColours(int hue)
        {
            if (hue == ColourProvider.Hue)
                return false;

            ColourProvider.ChangeColourScheme(hue);

            RecreateHeader();
            UpdateColours();
            return true;
        }

        private partial class ProfileSectionTabControl : OsuTabControl<ProfileSection>
        {
            public ProfileSectionTabControl()
            {
                Height = 40;
                Padding = new MarginPadding { Horizontal = HORIZONTAL_PADDING };
                TabContainer.Spacing = new Vector2(20);
            }

            protected override TabItem<ProfileSection> CreateTabItem(ProfileSection value) => new ProfileSectionTabItem(value);

            protected override bool OnClick(ClickEvent e) => true;

            protected override bool OnHover(HoverEvent e) => true;

            private partial class ProfileSectionTabItem : TabItem<ProfileSection>
            {
                private OsuSpriteText text = null!;

                [Resolved]
                private OverlayColourProvider colourProvider { get; set; } = null!;

                public ProfileSectionTabItem(ProfileSection value)
                    : base(value)
                {
                }

                [BackgroundDependencyLoader]
                private void load()
                {
                    AutoSizeAxes = Axes.Both;
                    Anchor = Anchor.CentreLeft;
                    Origin = Anchor.CentreLeft;

                    InternalChild = text = new OsuSpriteText
                    {
                        Text = Value.Title
                    };

                    updateState();
                }

                protected override void OnActivated() => updateState();

                protected override void OnDeactivated() => updateState();

                protected override bool OnHover(HoverEvent e)
                {
                    updateState();
                    return true;
                }

                protected override void OnHoverLost(HoverLostEvent e) => updateState();

                private void updateState()
                {
                    text.Font = OsuFont.Default.With(size: 14, weight: Active.Value ? FontWeight.SemiBold : FontWeight.Regular);

                    Colour4 textColour;

                    if (IsHovered)
                        textColour = colourProvider.Light1;
                    else
                        textColour = Active.Value ? colourProvider.Content1 : colourProvider.Light2;

                    text.FadeColour(textColour, 300, Easing.OutQuint);
                }
            }
        }

        private partial class ProfileSectionsContainer : SectionsContainer<ProfileSection>
        {
            private OverlayScrollContainer scroll = null!;

            public ProfileSectionsContainer()
            {
                RelativeSizeAxes = Axes.Both;
            }

            protected override UserTrackingScrollContainer CreateScrollContainer() => scroll = new OverlayScrollContainer();

            // Reverse child ID is required so expanding beatmap panels can appear above sections below them.
            // This can also be done by setting Depth when adding new sections above if using ReverseChildID turns out to have any issues.
            protected override FlowContainer<ProfileSection> CreateScrollContentContainer() => new ReverseChildIDFillFlowContainer<ProfileSection>
            {
                Direction = FillDirection.Vertical,
                AutoSizeAxes = Axes.Y,
                RelativeSizeAxes = Axes.X,
                Spacing = new Vector2(0, 10),
                Padding = new MarginPadding { Horizontal = 10 },
                Margin = new MarginPadding { Bottom = 10 },
            };

            protected override void LoadComplete()
            {
                base.LoadComplete();

                // Ensure the scroll-to-top button is displayed above the fixed header.
                AddInternal(scroll.Button.CreateProxy());
            }
        }
    }
}
