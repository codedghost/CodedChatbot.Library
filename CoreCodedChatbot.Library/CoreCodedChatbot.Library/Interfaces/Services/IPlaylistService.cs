using System.Collections.Generic;
using CoreCodedChatbot.ApiContract.Enums.Playlist;
using CoreCodedChatbot.ApiContract.ResponseModels.Playlist;
using CoreCodedChatbot.ApiContract.ResponseModels.Playlist.ChildModels;
using CoreCodedChatbot.Library.Models.Data;
using CoreCodedChatbot.Library.Models.Enums;
using CoreCodedChatbot.Library.Models.View;

namespace CoreCodedChatbot.Library.Interfaces.Services
{
    public interface IPlaylistService
    {
        PlaylistItem GetRequestById(int songId);
        (AddRequestResult, int) AddRequest(string username, string commandText, bool vipRequest = false);
        AddRequestResult AddSuperVipRequest(string username, string commandtext);
        AddRequestResult AddWebRequest(RequestSongViewModel requestSongViewModel, string username);
        PlaylistState GetPlaylistState();
        int PromoteRequest(string username);
        void UpdateFullPlaylist(bool updateCurrent = false);
        void ArchiveCurrentRequest(int songId = 0);
        string GetUserRequests(string username);
        List<string> GetUserRelevantRequests(string username);
        GetAllSongsResponse GetAllSongs();
        void ClearRockRequests();
        bool RemoveRockRequests(string username, string commandText, bool isMod);

        bool EditRequest(string username, string commandText, bool isMod, out string songRequestText,
            out bool syntaxError);

        EditRequestResult EditWebRequest(RequestSongViewModel editRequestModel, string username, bool isMod);
        PromoteRequestResult PromoteWebRequest(int songId, string username);

        bool AddSongToDrive(int songId);

        bool OpenPlaylist();
        bool ClosePlaylist();
        bool OpenPlaylistWeb();
        bool VeryClosePlaylistWeb();
        bool ArchiveRequestById(int songId);
        bool VeryClosePlaylist();
        int GetMaxUserRequests();
        bool IsSuperRequestInQueue();
        string EditSuperVipRequest(string username, string songText);
        bool RemoveSuperRequest(string username);
    }
}
