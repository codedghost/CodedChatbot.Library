using System;
using System.Collections.Generic;
using System.Linq;
using CoreCodedChatbot.ApiContract.Enums.Playlist;
using CoreCodedChatbot.Config;
using CoreCodedChatbot.Database.Context.Interfaces;
using CoreCodedChatbot.Database.Context.Models;
using CoreCodedChatbot.Library.Extensions;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Models.Data;
using CoreCodedChatbot.Library.Models.Enums;
using CoreCodedChatbot.Library.Models.SignalR;
using CoreCodedChatbot.Library.Models.View;
using CoreCodedChatbot.Secrets;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using TwitchLib.Api;
using TwitchLib.Client;

namespace CoreCodedChatbot.Library.Services
{
    public class PlaylistService : IPlaylistService
    {
        private const int UserMaxSongCount = 1;

        private readonly IChatbotContextFactory _contextFactory;
        private readonly IConfigService _configService;
        private readonly IVipService _vipService;
        private readonly ISecretService _secretService;
        private readonly TwitchClient _client;
        private readonly ILogger<IPlaylistService> _logger;

        private string _developmentRoomId;

        private PlaylistItem _currentRequest;
        private int _currentVipRequestsPlayed;
        private int _concurrentVipSongsToPlay;
        private Random _rand = new Random();

        public PlaylistService(IChatbotContextFactory contextFactory, IConfigService configService, IVipService vipService,
            ISecretService secretService, TwitchClient client,
            ILogger<IPlaylistService> logger)
        {
            this._contextFactory = contextFactory;
            _configService = configService;

            this._vipService = vipService;
            _secretService = secretService;

            this._client = client;
            _logger = logger;

            this._concurrentVipSongsToPlay = configService.Get<int>("ConcurrentRegularSongsToPlay");
        }

        public PlaylistItem GetRequestById(int songId)
        {
            using (var context = _contextFactory.Create())
            {
                var request = context.SongRequests.Find(songId);
                return new PlaylistItem
                {
                    songRequestId = request.SongRequestId,
                    songRequestText = request.RequestText,
                    songRequester = request.RequestUsername,
                    isInChat = (context.Users.SingleOrDefault(u => u.Username == request.RequestUsername)
                                    ?.TimeLastInChat ?? DateTime.MinValue)
                               .AddMinutes(2) >= DateTime.UtcNow ||
                               (request.VipRequestTime ?? DateTime.MinValue).AddMinutes(2) >=
                               DateTime.UtcNow ||
                               request.RequestTime.AddMinutes(5) >= DateTime.UtcNow,
                    isVip = request.VipRequestTime != null,
                    isSuperVip = request.SuperVipRequestTime != null,
                    isInDrive = request.InDrive
                };
            }
        }

        public (AddRequestResult, int) AddRequest(string username, string commandText, bool vipRequest = false)
        {
            var songIndex = 0;
            var playlistState = this.GetPlaylistState();
            if (playlistState == PlaylistState.VeryClosed) return (AddRequestResult.PlaylistVeryClosed, 0);

            using (var context = _contextFactory.Create())
            {
                var request = new SongRequest
                {
                    RequestTime = DateTime.UtcNow,
                    RequestText = commandText,
                    RequestUsername = username,
                    Played = false
                };

                if (!vipRequest)
                {
                    var playlistLength = context.SongRequests.Count(sr => !sr.Played);
                    var userSongCount = context.SongRequests.Count(sr => !sr.Played && sr.RequestUsername == username && sr.VipRequestTime == null);
                    _logger.LogInformation($"Not a vip request: {playlistLength}, {userSongCount}");
                    if (playlistState == PlaylistState.Closed)
                    {
                        return (AddRequestResult.PlaylistClosed, 0);
                    }

                    if (userSongCount >= UserMaxSongCount)
                    {
                        return (AddRequestResult.NoMultipleRequests, 0);
                    }
                }

                if (vipRequest) request.VipRequestTime = DateTime.UtcNow;

                context.SongRequests.Add(request);
                context.SaveChanges();

                songIndex = context.SongRequests.Where(sr => !sr.Played).OrderRequests()
                                .FindIndex(sr => sr == request) + 1;

                if (_currentRequest == null)
                {
                    _currentRequest = new PlaylistItem
                    {
                        songRequestId = request.SongRequestId,
                        songRequestText = request.RequestText,
                        songRequester = request.RequestUsername,
                        isEvenIndex = false,
                        isInChat = true,
                        isVip = vipRequest,
                        isSuperVip = false,
                        isInDrive = request.InDrive
                    };
                }
            }

            UpdatePlaylists();

            return (AddRequestResult.Success, songIndex);
        }

        public AddRequestResult AddSuperVipRequest(string username, string commandText)
        {
            var playlistState = GetPlaylistState();
            if (playlistState == PlaylistState.VeryClosed) return AddRequestResult.PlaylistVeryClosed;

            using (var context = _contextFactory.Create())
            {
                if (IsSuperRequestInQueue()) return AddRequestResult.OnlyOneSuper;

                var request = new SongRequest
                {
                    RequestTime = DateTime.UtcNow,
                    RequestText = commandText,
                    RequestUsername = username,
                    Played = false,
                    VipRequestTime = DateTime.UtcNow,
                    SuperVipRequestTime = DateTime.UtcNow
                };

                context.SongRequests.Add(request);
                context.SaveChanges();

                if (_currentRequest == null)
                {
                    _currentRequest = new PlaylistItem
                    {
                        songRequestId = request.SongRequestId,
                        songRequestText = request.RequestText,
                        songRequester = request.RequestUsername,
                        isEvenIndex = false,
                        isInChat = true,
                        isVip = true,
                        isSuperVip = true,
                        isInDrive = request.InDrive
                    };
                }
            }

            UpdatePlaylists();

            return AddRequestResult.Success;
        }

        public AddRequestResult AddWebRequest(RequestSongViewModel requestSongViewModel, string username)
        {
            try
            {
                if (string.IsNullOrEmpty(requestSongViewModel.Title) &&
                    string.IsNullOrWhiteSpace(requestSongViewModel.Artist)) return AddRequestResult.NoRequestEntered;

                var playlistState = GetPlaylistState();

                switch (playlistState)
                {
                    case PlaylistState.VeryClosed:
                        return AddRequestResult.PlaylistVeryClosed;
                    case PlaylistState.Closed when !requestSongViewModel.IsVip:
                        return AddRequestResult.PlaylistClosed;
                }

                using (var context = _contextFactory.Create())
                {
                    if (!requestSongViewModel.IsVip && !requestSongViewModel.IsSuperVip)
                    {
                        var userSongCount = context.SongRequests.Count(sr =>
                            !sr.Played && sr.RequestUsername == username && sr.VipRequestTime == null);

                        if (userSongCount >= UserMaxSongCount) return AddRequestResult.NoMultipleRequests;
                    }

                    var request = new SongRequest
                    {
                        RequestTime = DateTime.UtcNow,
                        RequestText =
                            $"{requestSongViewModel.Artist} - {requestSongViewModel.Title} ({requestSongViewModel.SelectedInstrument})",
                        RequestUsername = username,
                        Played = false
                    };

                    if (requestSongViewModel.IsSuperVip)
                    {
                        request.VipRequestTime = DateTime.UtcNow;
                        request.SuperVipRequestTime = DateTime.UtcNow;

                        if (!_vipService.UseSuperVip(username)) return AddRequestResult.UnSuccessful;
                    }
                    else if (requestSongViewModel.IsVip)
                    {
                        request.VipRequestTime = DateTime.UtcNow;
                        if (!_vipService.UseVip(username)) return AddRequestResult.UnSuccessful;
                    }

                    context.SongRequests.Add(request);
                    context.SaveChanges();
                }

                UpdatePlaylists();
            }
            catch (Exception)
            {
                return AddRequestResult.UnSuccessful;
            }

            return AddRequestResult.Success;
        }

        public PlaylistState GetPlaylistState()
        {
            using (var context = _contextFactory.Create())
            {
                var status = context.Settings
                    .SingleOrDefault(set => set.SettingName == "PlaylistStatus");
                if (status?.SettingValue == null)
                {
                    return PlaylistState.VeryClosed;
                }

                return Enum.Parse<PlaylistState>(status?.SettingValue);
            }
        }

        public int PromoteRequest(string username)
        {
            var newSongIndex = 0;
            using (var context = _contextFactory.Create())
            {
                var request = context.SongRequests.FirstOrDefault(sr => !sr.Played && sr.VipRequestTime == null && sr.RequestUsername == username);

                if (request == null)
                    return -1; // No request at this index

                if (request.RequestUsername != username)
                    return -2; // Not this users request.

                request.VipRequestTime = DateTime.UtcNow;
                context.SaveChanges();

                newSongIndex = context.SongRequests.Where(sr => !sr.Played).OrderRequests()
                                   .FindIndex(sr => sr == request) + 1;
            }

            UpdatePlaylists();
            return newSongIndex;
        }

        public async void UpdateFullPlaylist(bool updateCurrent = false)
        {
            var psk = _secretService.GetSecret<string>("SignalRKey");

            var connection = new HubConnectionBuilder()
                .WithUrl($"{_configService.Get<string>("WebPlaylistUrl")}/SongList")
                .Build();

            await connection.StartAsync();

            var requests = GetAllSongs();

            if (updateCurrent)
            {
                UpdateCurrentSong(requests.RegularList, requests.VipList);
            }

            requests.RegularList = requests.RegularList.Where(r => r.songRequestId != _currentRequest.songRequestId)
                .ToArray();
            requests.VipList = requests.VipList.Where(r => r.songRequestId != _currentRequest.songRequestId).ToArray();

            await connection.InvokeAsync<SongListHubModel>("SendAll",
                new SongListHubModel
                {
                    psk = psk,
                    currentSong = _currentRequest,
                    regularRequests = requests.RegularList,
                    vipRequests = requests.VipList
                });

            await connection.DisposeAsync();
        }

        public void ArchiveCurrentRequest(int songId = 0)
        {
            // SongId of zero indicates that the command has been called from twitch chat

            using (var context = _contextFactory.Create())
            {
                var currentRequest = songId == 0 ? _currentRequest :
                    songId == _currentRequest.songRequestId ? _currentRequest : null;

                if (currentRequest == null)
                    return;

                var currentRequestDbModel = context.SongRequests.Find(currentRequest.songRequestId);

                if (currentRequestDbModel == null)
                    return;

                _logger.LogInformation($"Removing request: {currentRequestDbModel.RequestText}");

                currentRequestDbModel.Played = true;
                context.SaveChanges();
            }

            UpdatePlaylists(true);
        }

        public string GetUserRequests(string username)
        {
            var relevantItems = GetUserRelevantRequests(username);

            return relevantItems.Any()
                ? string.Join(", ", relevantItems)
                : "Looks like you don't have any songs in the queue, get requestin' dude! <!rr>";
        }

        public List<string> GetUserRelevantRequests(string username)
        {
            using (var context = _contextFactory.Create())
            {
                var userRequests = context.SongRequests
                    .Where(sr => !sr.Played)
                    ?.OrderRequests()
                    ?.Select((sr, index) => new { Index = index + 1, SongRequest = sr })
                    ?.Where(x => x.SongRequest.RequestUsername == username)
                    ?.OrderBy(x => x.Index)
                    ?.Select(x =>
                        x.SongRequest.VipRequestTime != null
                            ? $"{x.Index} - {x.SongRequest.RequestText}"
                            : x.SongRequest.RequestText)
                    ?.ToList();

                return userRequests ?? new List<string>();
            }
        }

        public PlaylistViewModel GetAllSongs(LoggedInTwitchUser twitchUser = null)
        {
            using (var context = _contextFactory.Create())
            {
                var vipRequests = context.SongRequests.Where(sr => !sr.Played && sr.VipRequestTime != null)
                    .OrderRequests()
                    .Select((sr, index) =>
                    {
                        return new PlaylistItem
                        {
                            songRequestId = sr.SongRequestId,
                            songRequestText = sr.RequestText,
                            songRequester = sr.RequestUsername,
                            isInChat = (context.Users.SingleOrDefault(u => u.Username == sr.RequestUsername)?.TimeLastInChat ?? DateTime.MinValue)
                                       .AddMinutes(2) >= DateTime.UtcNow ||
                                       (sr.VipRequestTime ?? DateTime.MinValue).AddMinutes(5) >= DateTime.UtcNow,
                            isVip = sr.VipRequestTime != null,
                            isSuperVip = sr.SuperVipRequestTime != null,
                            isEvenIndex = index % 2 == 0,
                            isInDrive = sr.InDrive
                        };
                    })
                    .ToArray();

                var regularRequests = context.SongRequests.Where(sr => !sr.Played && sr.VipRequestTime == null)
                    .OrderRequests()
                    .Select((sr, index) =>
                    {
                        return new PlaylistItem
                        {
                            songRequestId = sr.SongRequestId,
                            songRequestText = sr.RequestText,
                            songRequester = sr.RequestUsername,
                            isInChat = (context.Users.SingleOrDefault(u => u.Username == sr.RequestUsername)
                                            ?.TimeLastInChat ?? DateTime.MinValue)
                                       .AddMinutes(2) >= DateTime.UtcNow ||
                                       sr.RequestTime.AddMinutes(5) >= DateTime.UtcNow,
                            isVip = sr.VipRequestTime != null,
                            isSuperVip = sr.SuperVipRequestTime != null,
                            isEvenIndex = index % 2 == 0,
                            isInDrive = sr.InDrive
                        };
                    }).ToArray();

                // Ensure if the playlist is populated then a request is made current
                if (_currentRequest == null)
                {
                    if (vipRequests.Any())
                    {
                        _currentRequest = vipRequests.First();
                        vipRequests = vipRequests.Where(r => r.songRequestId != _currentRequest.songRequestId).ToArray();
                    } else if (regularRequests.Any())
                    {
                        _currentRequest = regularRequests[_rand.Next(0, regularRequests.Length)];
                        regularRequests = regularRequests.Where(r => r.songRequestId != _currentRequest.songRequestId).ToArray();
                    }
                }

                return new PlaylistViewModel
                {
                    CurrentSong = _currentRequest,
                    RegularList = regularRequests,
                    VipList = vipRequests,
                    TwitchUser = twitchUser
                };
            }
        }

        public void ClearRockRequests()
        {
            using (var context = _contextFactory.Create())
            {
                var requests = context.SongRequests.Where(sr => !sr.Played);

                foreach (var request in requests)
                {
                    if (request.SuperVipRequestTime != null && request.SongRequestId != _currentRequest?.songRequestId)
                        _vipService.RefundSuperVip(request.RequestUsername, true);
                    else if (request.VipRequestTime != null && request.SongRequestId != _currentRequest?.songRequestId)
                        _vipService.RefundVip(request.RequestUsername, true);
                    if (request.SongRequestId == _currentRequest?.songRequestId)
                        _currentRequest = null;

                    request.Played = true;
                }

                context.SaveChanges();
            }

            UpdatePlaylists();
        }

        public bool RemoveRockRequests(string username, string commandText, bool isMod)
        {
            if (!int.TryParse(commandText.Trim(), out var playlistIndex))
            {
                // Try and find regular request
                using (var context = _contextFactory.Create())
                {
                    var userRequest = context.SongRequests
                        ?.FirstOrDefault(
                            sr => !sr.Played && sr.VipRequestTime == null && sr.RequestUsername == username);
                    if (userRequest == null) return false;

                    context.Remove(userRequest);
                    context.SaveChanges();
                    UpdatePlaylists();
                    return true;
                }
            }

            using (var context = _contextFactory.Create())
            {
                // We have a playlist number remove VIP
                var userRequest = context.SongRequests
                    ?.Where(sr => !sr.Played && sr.VipRequestTime != null)
                    ?.OrderRequests()
                    ?.Select((sr, index) => new { Index = index + 1, SongRequest = sr })
                    ?.Where(x => (x.SongRequest.RequestUsername == username || isMod) && x.Index == playlistIndex)
                    .FirstOrDefault();

                if (userRequest == null) return false;

                _vipService.RefundVip(userRequest.SongRequest.RequestUsername);

                context.Remove(userRequest.SongRequest);
                context.SaveChanges();
            }

            UpdatePlaylists();

            return true;
        }

        public bool EditRequest(string username, string commandText, bool isMod, out string songRequestText, out bool syntaxError)
        {
            var currentSongs = GetAllSongs();

            currentSongs.RegularList = currentSongs.RegularList.Where(r => r.songRequestId != _currentRequest.songRequestId)
                .ToArray();
            currentSongs.VipList = currentSongs.VipList.Where(r => r.songRequestId != _currentRequest.songRequestId).ToArray();

            var processEditArgsResponse = ProcessEditArgs(username, commandText, currentSongs, out songRequestText);

            if (processEditArgsResponse == ProcessEditArgsResult.ArgumentError ||
                processEditArgsResponse == ProcessEditArgsResult.NoRequestInList ||
                processEditArgsResponse == ProcessEditArgsResult.NoRequestProvided)
            {
                syntaxError = true;
                return false;
            }

            UpdatePlaylists();

            syntaxError = false;
            return true;
        }

        public EditRequestResult EditWebRequest(RequestSongViewModel editRequestModel, string username, bool isMod)
        {
            try
            {

                if (string.IsNullOrWhiteSpace(editRequestModel.Title) &&
                    string.IsNullOrWhiteSpace(editRequestModel.Artist))
                {
                    return EditRequestResult.NoRequestEntered;
                }

                using (var context = _contextFactory.Create())
                {
                    var songRequest =
                        context.SongRequests.SingleOrDefault(sr => sr.SongRequestId == editRequestModel.SongRequestId);

                    if (songRequest == null) return EditRequestResult.UnSuccessful;
                    if (songRequest.Played) return EditRequestResult.RequestAlreadyRemoved;
                    if (!isMod && songRequest.RequestUsername != username) return EditRequestResult.NotYourRequest;

                    songRequest.RequestText =
                        $"{editRequestModel.Artist} - {editRequestModel.Title} ({editRequestModel.SelectedInstrument})";
                    songRequest.InDrive = false;
                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Exception in EditWebRequest. editRequestModel: {editRequestModel}, username: {username}, isMod: {isMod}");
                return EditRequestResult.UnSuccessful;
            }
            
            UpdatePlaylists();

            return EditRequestResult.Success;
        }

        public PromoteRequestResult PromoteWebRequest(int songId, string username)
        {
            var vipIssued = false;
            try
            {
                using (var context = _contextFactory.Create())
                {
                    var songRequest = context.SongRequests.SingleOrDefault(sr => sr.SongRequestId == songId);

                    if (songRequest == null) return PromoteRequestResult.UnSuccessful;
                    if (songRequest.RequestUsername != username) return PromoteRequestResult.NotYourRequest;
                    if (songRequest.VipRequestTime != null) return PromoteRequestResult.AlreadyVip;
                    if (!_vipService.UseVip(username)) return PromoteRequestResult.NoVipAvailable;

                    vipIssued = true;
                    songRequest.VipRequestTime = DateTime.UtcNow;
                    context.SaveChanges();
                }
            }
            catch (Exception e)
            {
                if (vipIssued) _vipService.RefundVip(username);
                _logger.LogError(e, $"Exception in EditWebRequest. songId: {songId}, username: {username}");
                return PromoteRequestResult.UnSuccessful;
            }

            UpdatePlaylists();
            return PromoteRequestResult.Successful;
        }

        public bool AddSongToDrive(int songId)
        {
            using (var context = _contextFactory.Create())
            {
                var songRequest = context.SongRequests.SingleOrDefault(sr => sr.SongRequestId == songId);

                if (songRequest == null) return false;
                songRequest.InDrive = true;

                if (songRequest.SongRequestId == _currentRequest.songRequestId)
                    _currentRequest.isInDrive = true;

                context.SaveChanges();
            }

            UpdatePlaylists();
            return true;
        }

        public RequestSongViewModel GetNewRequestSongViewModel(string username)
        {
            return new RequestSongViewModel
            {
                ModalTitle = "Request a song",
                IsNewRequest = true,
                Title = string.Empty,
                Artist = string.Empty,
                Instruments = GetRequestInstruments(),
                SelectedInstrument = string.Empty,
                IsVip = false,
                IsSuperVip = false,
                ShouldShowVip = _vipService.HasVip(username),
                ShouldShowSuperVip = _vipService.HasSuperVip(username) && !IsSuperRequestInQueue()
            };
        }

        public bool IsSuperRequestInQueue()
        {
            using (var context = _contextFactory.Create())
            {
                return context.SongRequests.Any(sr => !sr.Played && sr.SuperVipRequestTime != null);
            }
        }

        public string EditSuperVipRequest(string username, string songText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(songText)) return string.Empty;

                using (var context = _contextFactory.Create())
                {
                    var usersSuperVip = context.SongRequests.SingleOrDefault(sr =>
                        !sr.Played && sr.RequestUsername == username && sr.SuperVipRequestTime != null);

                    if (usersSuperVip == null || usersSuperVip.SongRequestId == _currentRequest.songRequestId) return string.Empty;
                    
                    usersSuperVip.RequestText = songText;
                    context.SaveChanges();
                }

                UpdatePlaylists();

                return songText;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when editing a super vip request. username: {username}, songText: {songText}");
                return string.Empty;
            }
        }

        public bool RemoveSuperRequest(string username)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username)) return false;

                using (var context = _contextFactory.Create())
                {
                    var usersSuperVip = context.SongRequests.SingleOrDefault(sr =>
                        !sr.Played && sr.RequestUsername == username && sr.SuperVipRequestTime != null);

                    if (usersSuperVip == null || usersSuperVip.SongRequestId == _currentRequest.songRequestId)
                        return false;

                    if (!_vipService.RefundSuperVip(username)) return false;

                    usersSuperVip.Played = true;
                    context.SaveChanges();

                    UpdatePlaylists();

                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error when removing Super Vip request. username: {username}");
                return false;
            }
        }

        public RequestSongViewModel GetEditRequestSongViewModel(string username, int songRequestId, bool isMod)
        {
            using (var context = _contextFactory.Create())
            {
                var songRequest = context.SongRequests.SingleOrDefault(sr =>
                    !sr.Played && (sr.RequestUsername == username || isMod) && sr.SongRequestId == songRequestId);

                if (songRequest == null) return null;
                
                var formattedRequest = FormattedRequest.GetFormattedRequest(songRequest.RequestText);

                return new RequestSongViewModel
                {
                    ModalTitle = "Edit your request",
                    IsNewRequest = false,
                    SongRequestId = songRequest.SongRequestId,
                    Title = formattedRequest?.SongName ?? songRequest.RequestText,
                    Artist = formattedRequest?.SongArtist ?? string.Empty,
                    Instruments = GetRequestInstruments(formattedRequest?.InstrumentName),
                    SelectedInstrument = formattedRequest?.InstrumentName ?? "guitar",
                    IsVip = songRequest.VipRequestTime != null,
                    ShouldShowVip = false,
                    IsSuperVip = songRequest.SuperVipRequestTime != null,
                    ShouldShowSuperVip = false,
                };
            }
        }

        public bool OpenPlaylistWeb()
        {
            var isPlaylistOpened = OpenPlaylist();

            _client.SendMessage(string.IsNullOrEmpty(_developmentRoomId) ? _configService.Get<string>("StreamerChannel") : _developmentRoomId,
                isPlaylistOpened ? "The playlist is now open!" : "I couldn't open the playlist :(");

            return true;
        }

        public bool VeryClosePlaylistWeb()
        {
            var isPlaylistClosed = VeryClosePlaylist();

            _client.SendMessage(string.IsNullOrEmpty(_developmentRoomId) ? _configService.Get<string>("StreamerChannel") : _developmentRoomId,
                isPlaylistClosed ? "The playlist is now closed!" : "I couldn't close the playlist :(");

            return true;
        }

        public bool OpenPlaylist()
        {
            using (var context = _contextFactory.Create())
            {
                try
                {
                    var playlistStatusSetting = context.Settings
                        ?.SingleOrDefault(set => set.SettingName == "PlaylistStatus");

                    if (playlistStatusSetting == null)
                    {
                        playlistStatusSetting = new Setting
                        {
                            SettingName = "PlaylistStatus",
                            SettingValue = "Open"
                        };

                        context.Settings.Add(playlistStatusSetting);
                        context.SaveChanges();
                        return true;
                    }

                    playlistStatusSetting.SettingValue = "Open";
                    context.SaveChanges();
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public bool ClosePlaylist()
        {
            using (var context = _contextFactory.Create())
            {
                try
                {
                    var playlistStatusSetting = context.Settings
                        ?.SingleOrDefault(set => set.SettingName == "PlaylistStatus");

                    if (playlistStatusSetting == null)
                    {
                        playlistStatusSetting = new Setting
                        {
                            SettingName = "PlaylistStatus",
                            SettingValue = "Closed"
                        };

                        context.Settings.Add(playlistStatusSetting);
                        context.SaveChanges();
                        return true;
                    }

                    playlistStatusSetting.SettingValue = "Closed";
                    context.SaveChanges();
                    return true;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error when closing the playlist");
                    return false;
                }
            }
        }

        public bool ArchiveRequestById(int songId)
        {
            using (var context = _contextFactory.Create())
            {
                var request = context.SongRequests.Find(songId);

                if (request == null) return false;

                request.Played = true;

                if (request.SuperVipRequestTime != null)
                {
                    _vipService.RefundSuperVip(request.RequestUsername);
                }
                if (request.VipRequestTime != null)
                {
                    _vipService.RefundVip(request.RequestUsername);
                }

                context.SaveChanges();

                UpdatePlaylists();

                return true;
            }
        }

        public string GetEstimatedTime(ChatViewersModel chattersModel)
        {
            using (var context = _contextFactory.Create())
            {
                try
                {
                    var allViewers = chattersModel.chatters.viewers
                        .Concat(chattersModel.chatters.admins)
                        .Concat(chattersModel.chatters.global_mods)
                        .Concat(chattersModel.chatters.moderators)
                        .Concat(chattersModel.chatters.staff)
                        .ToArray();

                    var requests = context.SongRequests.Where(sr => !sr.Played)
                        .OrderRequests()
                        .Count(sr => allViewers.Contains(sr.RequestUsername));

                    var estimatedFinishTime = DateTime.UtcNow.AddMinutes(requests * 6d).ToString("HH:mm:ss");
                    return $"Estimated time to finish: {estimatedFinishTime}";
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error when calculating estimated time remaining in the playlist");
                    return string.Empty;
                }
            }
        }

        private void UpdateCurrentSong(PlaylistItem[] regularRequests, PlaylistItem[] vipRequests)
        {
            RequestTypes updateDecision;

            var inChatRegularRequests = regularRequests.Where(r => r.isInChat).ToList();
            if (!inChatRegularRequests.Any() && !vipRequests.Any())
            {
                _currentRequest = null;
                return;
            }

            if (_currentRequest.isVip)
            {
                _currentVipRequestsPlayed++;
                if (vipRequests.Any(vr => vr.isSuperVip))
                {
                    updateDecision = RequestTypes.SuperVip;
                }
                else if (_currentVipRequestsPlayed < _concurrentVipSongsToPlay
                    && vipRequests.Any())
                {
                    updateDecision = RequestTypes.Vip;
                }
                else if (inChatRegularRequests.Any())
                {
                    _currentVipRequestsPlayed = 0;
                    updateDecision = RequestTypes.Regular;
                }
                else if (vipRequests.Any())
                {
                    updateDecision = RequestTypes.Vip;
                }
                else
                {
                    updateDecision = RequestTypes.Empty;
                }
            }
            else
            {
                if (vipRequests.Any(vr => vr.isSuperVip))
                {
                    updateDecision = RequestTypes.SuperVip;
                }
                else if (vipRequests.Any())
                {
                    updateDecision = RequestTypes.Vip;
                }
                else if (inChatRegularRequests.Any())
                {
                    updateDecision = RequestTypes.Regular;
                }
                else
                {
                    updateDecision = RequestTypes.Empty;
                }
            }

            switch (updateDecision)
            {
                case RequestTypes.Regular:
                    _currentRequest = inChatRegularRequests[_rand.Next(inChatRegularRequests.Count)];
                    break;
                case RequestTypes.Vip:
                    _currentRequest = vipRequests.FirstOrDefault();
                    break;
                case RequestTypes.SuperVip:
                    _currentRequest = vipRequests.FirstOrDefault(vr => vr.isSuperVip);
                    break;
                case RequestTypes.Empty:
                    _currentRequest = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void UpdatePlaylists(bool updateCurrent = false)
        {
            UpdateFullPlaylist(updateCurrent);
        }

        public bool VeryClosePlaylist()
        {
            using (var context = _contextFactory.Create())
            {
                try
                {
                    var playlistStatusSetting = context.Settings
                        ?.SingleOrDefault(set => set.SettingName == "PlaylistStatus");

                    if (playlistStatusSetting == null)
                    {
                        playlistStatusSetting = new Setting
                        {
                            SettingName = "PlaylistStatus",
                            SettingValue = "VeryClosed"
                        };

                        context.Settings.Add(playlistStatusSetting);
                        context.SaveChanges();
                        return true;
                    }

                    playlistStatusSetting.SettingValue = "VeryClosed";
                    context.SaveChanges();
                    return true;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error encountered when closing the playlist completely");
                    return false;
                }
            }
        }

        public int GetMaxUserRequests()
        {
            return UserMaxSongCount;
        }

        private SelectListItem[] GetRequestInstruments(string selectedInstrumentName = "guitar")
        {
            var instrumentName = string.IsNullOrWhiteSpace(selectedInstrumentName) ? "guitar" : selectedInstrumentName;
            return new []
            {
                new SelectListItem("Guitar", "guitar", instrumentName == "guitar"),
                new SelectListItem("Bass", "bass", instrumentName == "bass"), 
            };
        }

        private bool ProcessEdit(string songRequestText, PlaylistViewModel currentSongs, string username, ProcessEditArgsResult action, int songIndex = 0)
        {
            var vipRequestsWithIndex =
                currentSongs.VipList.Select((sr, index) => new { Index = index + 1, SongRequest = sr }).ToList();

            using (var context = _contextFactory.Create())
            {
                PlaylistItem request;
                switch (action)
                {
                    case ProcessEditArgsResult.OneRequestEdit:
                        request = currentSongs.RegularList.SingleOrDefault(rs => rs.songRequester == username) ??
                                      currentSongs.VipList.SingleOrDefault(vs => vs.songRequester == username);

                        break;
                    case ProcessEditArgsResult.RegularRequest:
                        request = currentSongs.RegularList.SingleOrDefault(rs => rs.songRequester == username);

                        break;
                    case ProcessEditArgsResult.VipRequestNoIndex:
                        request = currentSongs.VipList.SingleOrDefault(rs => rs.songRequester == username);

                        break;
                    case ProcessEditArgsResult.VipRequestWithIndex:
                        request = vipRequestsWithIndex
                            .SingleOrDefault(rs => rs.SongRequest.songRequester == username && rs.Index == songIndex)?
                            .SongRequest;

                        break;
                    default:
                        request = null;
                        break;
                }

                if (request == null) return false;

                var dbReq = context.SongRequests.SingleOrDefault(
                    sr => sr.SongRequestId == request.songRequestId);

                if (dbReq == null) return false;

                dbReq.RequestText = songRequestText;
                dbReq.InDrive = false;
                context.SaveChanges();

                return true;
            }

        }

        private ProcessEditArgsResult ProcessEditArgs(string username, string commandText, PlaylistViewModel currentSongs, out string songRequestText)
        {
            var vipRequestsWithIndex =
                currentSongs.VipList.Select((sr, index) => new { Index = index + 1, SongRequest = sr }).ToList();

            songRequestText = string.Empty;

            if (currentSongs.RegularList.All(rs => rs.songRequester != username) &&
                currentSongs.VipList.All(vs => vs.songRequester != username))
            {
                return ProcessEditArgsResult.NoRequestInList;
            }

            var splitCommandText = commandText.Split(' ').ToList();
            int.TryParse(splitCommandText[0].Trim(), out var playlistIndex);
            if (playlistIndex != 0)
            {
                splitCommandText.RemoveAt(0);
            }

            songRequestText = string.Join(" ", splitCommandText);

            var totalRequestCount = (currentSongs.RegularList.Count(rs => rs.songRequester == username) +
                                    vipRequestsWithIndex.Count(vs => vs.SongRequest.songRequester == username));

            var doesUserHaveRegularRequest = currentSongs.RegularList.Any(rs => rs.songRequester == username);
            var userVips = vipRequestsWithIndex.Where(req => req.SongRequest.songRequester == username).ToList();

            if (string.IsNullOrWhiteSpace(songRequestText))
            {
                return ProcessEditArgsResult.NoRequestProvided;
            }

            if (totalRequestCount == 1)
            {
                return ProcessEdit(songRequestText, currentSongs, username, ProcessEditArgsResult.OneRequestEdit)
                    ? ProcessEditArgsResult.OneRequestEdit
                    : ProcessEditArgsResult.ArgumentError; // We can change this request regardless
            }

            if (playlistIndex != 0)
            {
                if (userVips.Count == 0)
                {
                    return ProcessEditArgsResult.ArgumentError;
                }

                return ProcessEdit(songRequestText, currentSongs, username, ProcessEditArgsResult.VipRequestWithIndex,
                    playlistIndex)
                    ? ProcessEditArgsResult.VipRequestWithIndex
                    : ProcessEditArgsResult.ArgumentError;
            }


            if (doesUserHaveRegularRequest)
            {
                return ProcessEdit(songRequestText, currentSongs, username, ProcessEditArgsResult.RegularRequest)
                    ? ProcessEditArgsResult.RegularRequest
                    : ProcessEditArgsResult.ArgumentError;
            }
            switch (userVips.Count)
            {
                case 1:
                    return ProcessEdit(songRequestText, currentSongs, username, ProcessEditArgsResult.VipRequestNoIndex)
                        ? ProcessEditArgsResult.VipRequestNoIndex
                        : ProcessEditArgsResult.ArgumentError;
                default:
                    return ProcessEditArgsResult.ArgumentError;
            }
        }
    }
}
