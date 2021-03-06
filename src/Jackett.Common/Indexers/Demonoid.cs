﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CsQuery;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Indexers
{
    public class Demonoid : BaseWebIndexer
    {
        private string LoginUrl { get { return SiteLink + "account_handler.php"; } }
        private string SearchUrl { get { return SiteLink + "files/?category={0}&subcategory=All&quality=All&seeded=2&to=1&query={1}&external=2"; } }

        private new ConfigurationDataRecaptchaLogin configData
        {
            get { return (ConfigurationDataRecaptchaLogin)base.configData; }
            set { base.configData = value; }
        }

        public Demonoid(IIndexerConfigurationService configService, Utils.Clients.WebClient wc, Logger l, IProtectionService ps)
            : base(name: "Demonoid",
                description: "Demonoid is a Private torrent tracker for 0DAY / TV / MOVIES / GENERAL",
                link: "https://www.demonoid.pw/",
                caps: TorznabUtil.CreateDefaultTorznabTVCaps(),
                configService: configService,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationDataRecaptchaLogin())
        {
            Encoding = Encoding.UTF8;
            Language = "en-us";
            Type = "public";

            AddCategoryMapping(5, TorznabCatType.PC0day, "Applications");
            AddCategoryMapping(17, TorznabCatType.AudioAudiobook, "Audio Books");
            AddCategoryMapping(11, TorznabCatType.Books, "Books");
            AddCategoryMapping(10, TorznabCatType.BooksComics, "Comics");
            AddCategoryMapping(4, TorznabCatType.PCGames, "Games");
            AddCategoryMapping(9, TorznabCatType.TVAnime, "Japanese Anime");
            AddCategoryMapping(6, TorznabCatType.Other, "Miscellaneous");
            AddCategoryMapping(1, TorznabCatType.Movies, "Movies");
            AddCategoryMapping(2, TorznabCatType.Audio, "Music");
            AddCategoryMapping(13, TorznabCatType.AudioVideo, "Music Videos");
            AddCategoryMapping(8, TorznabCatType.Other, "Pictures");
            AddCategoryMapping(3, TorznabCatType.TV, "TV");
        }

        public override async Task<ConfigurationData> GetConfigurationForSetup()
        {
            var loginPage = await RequestStringWithCookies(LoginUrl, string.Empty);
            CQ cq = loginPage.Content;
            var captcha = cq.Find(".g-recaptcha");
            if (captcha.Any())
            {
                var result = this.configData;
                result.CookieHeader.Value = loginPage.Cookies;
                result.Captcha.SiteKey = captcha.Attr("data-sitekey");
                result.Captcha.Version = "2";
                return result;
            }
            else
            {
                var result = new ConfigurationDataBasicLogin();
                result.SiteLink.Value = configData.SiteLink.Value;
                result.Instructions.Value = configData.Instructions.Value;
                result.Username.Value = configData.Username.Value;
                result.Password.Value = configData.Password.Value;
                result.CookieHeader.Value = loginPage.Cookies;
                return result;
            }
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            var pairs = new Dictionary<string, string> {
                { "nickname", configData.Username.Value },
                { "password", configData.Password.Value },
                { "rcaptcha", configData.Captcha.Value },
                { "returnpath", "/" },
                { "withq", "0" },
                { "re_ch", "" },
                { "validation", "" },
                { "Submit", "Submit" }
            };

            if (!string.IsNullOrWhiteSpace(configData.Captcha.Cookie))
            {
                CookieHeader = configData.Captcha.Cookie;
                try
                {
                    var results = await PerformQuery(new TorznabQuery());
                    if (results.Count() == 0)
                    {
                        throw new Exception("Your cookie did not work");
                    }

                    IsConfigured = true;
                    SaveConfig();
                    return IndexerConfigurationStatus.Completed;
                }
                catch (Exception e)
                {
                    IsConfigured = false;
                    throw new Exception("Your cookie did not work: " + e.Message);
                }
            }

            var result = await RequestLoginAndFollowRedirect(LoginUrl, pairs, null, true, SiteLink, SiteLink);
            await ConfigureIfOK(result.Cookies, result.Content != null && result.Cookies.Contains("uid="), () =>
            {
                CQ dom = result.Content;
                string errorMessage = dom["form[id='bb_code_form']"].Parent().Find("font[class='red']").Text();
                throw new ExceptionWithConfigData(errorMessage, configData);
            });
            return IndexerConfigurationStatus.RequiresTesting;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();
            var trackerCats = MapTorznabCapsToTrackers(query);
            var cat = (trackerCats.Count == 1 ? trackerCats.ElementAt(0) : "0");
            var episodeSearchUrl = string.Format(SearchUrl, cat, WebUtility.UrlEncode(query.GetQueryString()));
            var results = await RequestStringWithCookiesAndRetry(episodeSearchUrl);

            if (results.IsRedirect)
            {
                throw new ExceptionWithConfigData("Unexpected redirect to " + results.RedirectingTo + ". Check your credentials.", configData);
            }

            if (results.Content.Contains("No torrents found"))
            {
                return releases;
            }

            try
            {
                CQ dom = results.Content;
                var rows = dom[".ctable_content_no_pad > table > tbody > tr"].ToArray();
                DateTime lastDateTime = default(DateTime);
                for (var i = 0; i < rows.Length; i++)
                {
                    var rowA = rows[i];
                    var rAlign = rowA.Attributes["align"];
                    if (rAlign == "right" || rAlign == "center")
                        continue;
                    if (rAlign == "left")
                    {
                        // ex: "Monday, Jun 01, 2015", "Monday, Aug 03, 2015"
                        var dateStr = rowA.Cq().Text().Trim().Replace("Added on ", "");
                        if (string.IsNullOrWhiteSpace(dateStr) || 
                            dateStr == "Sponsored links" ||
                            dateStr.StartsWith("!function") ||
                            dateStr.StartsWith("atOptions"))
                        {
                            continue; // ignore ads
                        }
                        if (dateStr.ToLowerInvariant().Contains("today"))
                            lastDateTime = DateTime.Now;
                        else
                            lastDateTime = DateTime.SpecifyKind(DateTime.ParseExact(dateStr, "dddd, MMM dd, yyyy", CultureInfo.InvariantCulture), DateTimeKind.Utc).ToLocalTime();
                        continue;
                    }
                    if (rowA.ChildElements.Count() < 2)
                        continue;

                    var rowB = rows[++i];

                    var release = new ReleaseInfo();
                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800;

                    release.PublishDate = lastDateTime;

                    var catUrl = rowA.ChildElements.ElementAt(0).FirstElementChild.GetAttribute("href");
                    var catId = QueryHelpers.ParseQuery(catUrl)["category"].First();
                    release.Category = MapTrackerCatToNewznab(catId);

                    var qLink = rowA.ChildElements.ElementAt(1).FirstElementChild.Cq();
                    release.Title = qLink.Text().Trim();
                    release.Description = rowB.ChildElements.ElementAt(0).Cq().Text();

                    if (release.Category != null && release.Category.Contains(TorznabCatType.Audio.ID))
                    {
                        if (release.Description.Contains("Lossless"))
                            release.Category = new List<int> { TorznabCatType.AudioLossless.ID };
                        else if (release.Description.Contains("MP3"))
                            release.Category = new List<int> { TorznabCatType.AudioMP3.ID };
                        else
                            release.Category = new List<int> { TorznabCatType.AudioOther.ID };
                    }

                    release.Comments = new Uri(new Uri(SiteLink), qLink.Attr("href"));
                    release.Guid = release.Comments;
                    release.Link = release.Comments; // indirect download see Download() method

                    var sizeStr = rowB.ChildElements.ElementAt(2).Cq().Text();
                    release.Size = ReleaseInfo.GetBytes(sizeStr);

                    release.Seeders = ParseUtil.CoerceInt(rowB.ChildElements.ElementAt(5).Cq().Text());
                    release.Peers = ParseUtil.CoerceInt(rowB.ChildElements.ElementAt(6).Cq().Text()) + release.Seeders;

                    var grabs = rowB.Cq().Find("td:nth-child(5)").Text();
                    release.Grabs = ParseUtil.CoerceInt(grabs);

                    release.DownloadVolumeFactor = 0; // ratioless
                    release.UploadVolumeFactor = 1;

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }
            return releases;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            var results = await RequestStringWithCookies(link.AbsoluteUri);
            //await FollowIfRedirect(results); // manual follow for better debugging (string)
            if (results.IsRedirect)
                results = await RequestStringWithCookies(results.RedirectingTo);
            CQ dom = results.Content;
            var dl = dom.Find("a:has(font:contains(\"Download torrent file\"))");

            link = new Uri(dl.Attr("href"));

            return await base.Download(link);
        }
    }
}
