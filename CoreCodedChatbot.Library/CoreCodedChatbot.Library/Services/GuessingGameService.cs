﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreCodedChatbot.Config;
using CoreCodedChatbot.Database.Context.Interfaces;
using CoreCodedChatbot.Database.Context.Models;
using CoreCodedChatbot.Library.Interfaces.Services;
using CoreCodedChatbot.Library.Models.Data;
using Microsoft.Extensions.Logging;
using TwitchLib.Api;
using TwitchLib.Client;

namespace CoreCodedChatbot.Library.Services
{
    public class GuessingGameService : IGuessingGameService
    {
        private readonly TwitchClient Client;
        private readonly TwitchAPI Api;
        private readonly IConfigService _configService;
        private readonly ILogger<IGuessingGameService> _logger;
        private IChatbotContextFactory contextFactory;

        private string _streamerChannel;
        private int _secondsForGuessingGame;

        private string DevelopmentRoomId;

        private const string GuessingGameStateSettingKey = "IsGuessingGameInProgress";
        
        public GuessingGameService(IChatbotContextFactory contextFactory,
            TwitchClient client, TwitchAPI api, IConfigService configService,
            ILogger<IGuessingGameService> logger)
        {
            this.contextFactory = contextFactory;
            this.Client = client;
            this.Api = api;
            _configService = configService;
            _logger = logger;

            _streamerChannel = _configService.Get<string>("StreamerChannel");
            _secondsForGuessingGame = configService.Get<int>("SecondsForGuessingGame");

            if (_configService.Get<bool>("DevelopmentBuild"))
            {
                Api.V5.Chat.GetChatRoomsByChannelAsync(_configService.Get<string>("ChannelId"), _configService.Get<string>("ChatbotAccessToken"))
                    .ContinueWith(
                        rooms =>
                        {
                            if (!rooms.IsCompletedSuccessfully) return;
                            DevelopmentRoomId = rooms.Result.Rooms.SingleOrDefault(r => r.Name == "dev")?.Id;
                        });
            }
        }

        public void GuessingGameStart(string songName, int songLengthInSeconds)
        {
            InitialiseGameTimer(songName, songLengthInSeconds);
        }

        private async void InitialiseGameTimer(string songName, int songLengthInSeconds)
        {
            if (_configService.Get<bool>("DevelopmentBuild") && !Client.JoinedChannels.Select(jc => jc.Channel)
                    .Any(jc => jc.Contains(DevelopmentRoomId)))
                Client.JoinRoom(_configService.Get<string>("ChannelId"), DevelopmentRoomId);

            if (songLengthInSeconds < _secondsForGuessingGame)
            {
                return;
            }

            if (!OpenGuessingGame(songName))
            {
                
                Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId, "I couldn't start the guessing game :S");
                return;
            }

            SetGuessingGameState(true);

            Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId,
                $"The guessing game has begun! You have {_secondsForGuessingGame} seconds to !guess the accuracy that {_streamerChannel} will get on {songName}!");

            await Task.Delay(TimeSpan.FromSeconds(_secondsForGuessingGame));

            if (!CloseGuessingGame())
            {
                Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId, "I couldn't close the guessing game for some reason... SEND HALP");
            }

            Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId, "The guessing game has now closed. Good luck everyone!");

            SetGuessingGameState(false);
        }

        private bool OpenGuessingGame(string songName)
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    CloseExistingGames(context);

                    var newGuessRecord = new SongGuessingRecord
                    {
                        SongDetails = songName,
                        UsersCanGuess = true,
                        IsInProgress = true
                    };

                    context.SongGuessingRecords.Add(newGuessRecord);
                    context.SaveChanges();

                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Encountered an error, returning false. SongName: {songName}");
                return false;
            }
        }

        private bool CloseGuessingGame()
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    var currentGuessingGameRecords = context.SongGuessingRecords.Where(x => x.UsersCanGuess);

                    if (!currentGuessingGameRecords.Any())
                    {
                        _logger.LogInformation("Looks like this game is already closed");
                        return false;
                    }

                    var currentGuessingGameRecord = currentGuessingGameRecords.FirstOrDefault();

                    if (currentGuessingGameRecord == null)
                    {
                        _logger.LogWarning("This really shouldn't happen, currentGuessingGameRecord is null");
                        return false;
                    }

                    currentGuessingGameRecord.UsersCanGuess = false;
                    context.SaveChanges();

                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Encountered an error while closing the guessing game, returning false");
                return false;
            }
        }

        public bool SetPercentageAndFinishGame(decimal finalPercentage)
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    var currentGuessingGameRecord = context.SongGuessingRecords.SingleOrDefault(x => x.IsInProgress);

                    if (currentGuessingGameRecord == null)
                    {
                        _logger.LogWarning(
                            "Either the game has already been completed or there is currently more than 1 guessing game running");
                        return false;
                    }

                    currentGuessingGameRecord.IsInProgress = false;
                    currentGuessingGameRecord.FinalPercentage = finalPercentage;
                    context.SaveChanges();

                    // Find closest guess.
                    AnnounceCurrentGameWinner(currentGuessingGameRecord, context);
                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Encountered an error when setting percentage and finishing guessing game. finalPercentage: {finalPercentage}");
                return false;
            }
        }

        private void AnnounceCurrentGameWinner(SongGuessingRecord currentGuessingRecord, IChatbotContext context)
        {
            var potentialWinnerModels = context.SongPercentageGuesses
                .Where(g => g.SongGuessingRecord.SongGuessingRecordId == currentGuessingRecord.SongGuessingRecordId).ToList()
                .Select(pg =>
                (Math.Floor(Math.Abs(currentGuessingRecord.FinalPercentage - pg.Guess) * 10) / 10, pg)).ToList();

            // No-one guessed?
            if (!potentialWinnerModels.Any())
                Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId, "Nobody guessed! Good luck next time :)");

            var winners = GuessingGameWinner.Create(potentialWinnerModels);

            // TODO: URGENT -> Refactor this to own service when bytes service is brought over to library project.
            GiveBytes(winners.Where(w => w.Difference <= 20).ToList());
            
            Client.SendMessage(string.IsNullOrEmpty(DevelopmentRoomId) ? _streamerChannel : DevelopmentRoomId,
                winners[0].Difference > 20 ?
                    $"@{string.Join(", @", winners.Select(w=> w.Username))} has won... nothing!" +
                    $" {string.Join(", ", winners.Select(w => $"{w.Username} guessed {w.Guess}%"))} " +
                    $"You were {winners[0].Difference} away from the actual score. Do you even know {_streamerChannel}?"
                    : winners[0].Difference == 0
                        ? $"@{string.Join(", @", winners.Select(w => w.Username))} has won! You guessed {winners[0].Guess}. You were spot on! You've received {winners[0].BytesWon} bytes"
                        : $"@{string.Join(", @", winners.Select(w => w.Username))} has won! " +
                            $"{string.Join(", ", winners.Select(w => $"{w.Username} guessed {w.Guess}% "))}" +
                            $"You were {winners[0].Difference} away from the actual score. You've received {winners[0].BytesWon} bytes");
        }

        private void GiveBytes(List<GuessingGameWinner> winners)
        {
            using (var context = contextFactory.Create())
            {
                foreach (var winner in winners)
                {
                    // Find or add user
                    var user = context.Users.Find(winner.Username);

                    if (user == null)
                    {
                        user = new User
                        {
                            Username = winner.Username.ToLower(),
                            UsedVipRequests = 0,
                            ModGivenVipRequests = 0,
                            FollowVipRequest = 0,
                            SubVipRequests = 0,
                            DonationOrBitsVipRequests = 0,
                            TokenBytes = 0
                        };

                        context.Users.Add(user);
                    }

                    user.TokenBytes += (int) Math.Round(winner.BytesWon * _configService.Get<int>("BytesToVip"));
                }

                context.SaveChanges();
            }
        }

        private int GetRunningGameId()
        {
            using (var context = contextFactory.Create())
            {
                return GetRunningGameId(context);
            }
        }

        private int GetRunningGameId(IChatbotContext context)
        {
            return context.SongGuessingRecords?.SingleOrDefault(x => x.UsersCanGuess)?.SongGuessingRecordId ?? 0;
        }

        public bool UserGuess(string username, decimal percentageGuess)
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    var currentGameId = GetRunningGameId(context);

                    if (currentGameId == 0) return false;

                    var existingGuess = context.SongPercentageGuesses.SingleOrDefault(g =>
                        g.SongGuessingRecordId == currentGameId && g.Username == username);

                    
                    if (existingGuess == null)
                    {
                        existingGuess = new SongPercentageGuess
                        {
                            Guess = percentageGuess,
                            SongGuessingRecordId = currentGameId,
                            Username = username
                        };
                        context.SongPercentageGuesses.Add(existingGuess);
                    }
                    else
                    {
                        existingGuess.Guess = percentageGuess;
                    }
                    context.SaveChanges();

                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogInformation(e,
                    $"Encountered an error while trying to record a user's guess for the guessing game. username: {username}, percentageguess: {percentageGuess}");
                return false;
            }
        }

        private void CloseExistingGames(IChatbotContext context)
        {
            // Unlikely, but ensure that other open games are closed

            var unclosedGames = context.SongGuessingRecords.Where(x => x.UsersCanGuess || x.IsInProgress);

            foreach (var unclosedGame in unclosedGames)
            {
                unclosedGame.UsersCanGuess = false;
                unclosedGame.IsInProgress = false;
            }

            context.SaveChanges();
        }
        
        public bool IsGuessingGameInProgress()
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    var guessingGameVal =
                        context.Settings.SingleOrDefault(s => s.SettingName == GuessingGameStateSettingKey);

                    if (guessingGameVal == null)
                    {
                        guessingGameVal = new Setting
                        {
                            SettingName = GuessingGameStateSettingKey,
                            SettingValue = "false"
                        };

                        context.Settings.Add(guessingGameVal);
                        context.SaveChanges();
                    }

                    return guessingGameVal.SettingValue == "true";
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error encountered when checking if there was a guessing game in progress.");
                return true;
            }
        }

        public bool SetGuessingGameState(bool state)
        {
            try
            {
                using (var context = contextFactory.Create())
                {
                    var currentState =
                        context.Settings.SingleOrDefault(s => s.SettingName == GuessingGameStateSettingKey);

                    if (currentState == null)
                    {
                        currentState = new Setting
                        {
                            SettingName = GuessingGameStateSettingKey
                        };

                        context.Settings.Add(currentState);
                    }

                    currentState.SettingValue = state ? "true" : "false";
                    context.SaveChanges();
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error encountered when setting the GuessingGameState. State: {state}");
                return false;
            }
        }
    }
}