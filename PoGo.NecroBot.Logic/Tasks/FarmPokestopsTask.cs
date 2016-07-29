﻿#region using directives

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GeoCoordinatePortable;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Map.Fort;
using POGOProtos.Networking.Responses;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class FarmPokestopsTask
    {
        public static int TimesZeroXPawarded;

        public static async Task Execute(ISession session)
        {
            var distanceFromStart = LocationUtils.CalculateDistanceInMeters(
                session.Settings.DefaultLatitude, session.Settings.DefaultLongitude,
                session.Client.CurrentLatitude, session.Client.CurrentLongitude);

            // Edge case for when the client somehow ends up outside the defined radius
            if (session.LogicSettings.MaxTravelDistanceInMeters != 0 &&
                distanceFromStart > session.LogicSettings.MaxTravelDistanceInMeters)
            {
                Logger.Write(
                    session.Translation.GetTranslation(TranslationString.FarmPokestopsOutsideRadius, distanceFromStart),
                    LogLevel.Warning);

                await Task.Delay(1000);

                await session.Navigation.HumanLikeWalking(
                    new GeoCoordinate(session.Settings.DefaultLatitude, session.Settings.DefaultLongitude),
                    session.LogicSettings.WalkingSpeedInKilometerPerHour, null);
            }

            var pokestopList = await GetPokeStops(session);
            var stopsHit = 0;
            var eggWalker = new EggWalker(1000, session);

            if (pokestopList.Count <= 0)
            {
                session.EventDispatcher.Send(new WarnEvent
                {
                    Message = session.Translation.GetTranslation(TranslationString.FarmPokestopsNoUsableFound)
                });
            }

            session.EventDispatcher.Send(new PokeStopListEvent {Forts = pokestopList});

            while (pokestopList.Any())
            {
                //resort
                pokestopList =
                    pokestopList.OrderBy(
                        i =>
                            LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                                session.Client.CurrentLongitude, i.Latitude, i.Longitude)).ToList();
                var pokeStop = pokestopList[0];
                pokestopList.RemoveAt(0);

                var distance = LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude, pokeStop.Latitude, pokeStop.Longitude);
                var fortInfo = await session.Client.Fort.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                session.EventDispatcher.Send(new FortTargetEvent {Name = fortInfo.Name, Distance = distance});

                await session.Navigation.HumanLikeWalking(new GeoCoordinate(pokeStop.Latitude, pokeStop.Longitude),
                    session.LogicSettings.WalkingSpeedInKilometerPerHour,
                    async () =>
                    {
                        // Catch normal map Pokemon
                        await CatchNearbyPokemonsTask.Execute(session);
                        //Catch Incense Pokemon
                        await CatchIncensePokemonsTask.Execute(session);
                        return true;
                    });

                //Catch Lure Pokemon
                if (pokeStop.LureInfo != null)
                {
                    await CatchLurePokemonsTask.Execute(session, pokeStop);
                }

                FortSearchResponse fortSearch;
                var TimesZeroXPawarded = 0;
                var fortTry = 0;      //Current check
                const int retryNumber = 50; //How many times it needs to check to clear softban
                const int zeroCheck = 5; //How many times it checks fort before it thinks it's softban
                do {
                    fortSearch = await session.Client.Fort.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                    if (fortSearch.ExperienceAwarded > 0 && TimesZeroXPawarded > 0) TimesZeroXPawarded = 0;
                    if (fortSearch.ExperienceAwarded == 0)
                    {
                        TimesZeroXPawarded++;

                        if (TimesZeroXPawarded > zeroCheck)
                        {
                            if ((int)fortSearch.CooldownCompleteTimestampMs != 0)
                            {
                                break; // Check if successfully looted, if so program can continue as this was "false alarm".
                            }

                            fortTry += 1;

                            session.EventDispatcher.Send(new FortFailedEvent
                            {
                                Name = fortInfo.Name,
                                Try = fortTry,
                                Max = retryNumber - zeroCheck
                            });

                            DelayingUtils.Delay(session.LogicSettings.DelayBetweenPlayerActions, 400);
                        }
                    } else {
                        session.EventDispatcher.Send(new FortUsedEvent
                        {
                            Id = pokeStop.Id,
                            Name = fortInfo.Name,
                            Exp = fortSearch.ExperienceAwarded,
                            Gems = fortSearch.GemsAwarded,
                            Items = StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded),
                            Latitude = pokeStop.Latitude,
                            Longitude = pokeStop.Longitude,
                            inventoryFull = (fortSearch.Result == FortSearchResponse.Types.Result.InventoryFull)
                        });

                        break; //Continue with program as loot was succesfull.
                    }
                    } while (fortTry < retryNumber - zeroCheck); //Stop trying if softban is cleaned earlier or if 40 times fort looting failed.

                await Task.Delay(1000);

                await eggWalker.ApplyDistance(distance);

                if (session.LogicSettings.SnipeAtPokestops)
                {
                    await SnipePokemonTask.Execute(session);
                }

                if (++stopsHit%5 == 0) //TODO: OR item/pokemon bag is full
                {
                    stopsHit = 0;
                    if (fortSearch.ItemsAwarded.Count > 0)
                    {
                        await session.Inventory.RefreshCachedInventory();
                    }
                    await RecycleItemsTask.Execute(session);

                    if (session.LogicSettings.EvolveAllPokemonWithEnoughCandy || session.LogicSettings.EvolveAllPokemonAboveIv)
                    {
                        await EvolvePokemonTask.Execute(session);
                    }
                    if (session.LogicSettings.TransferDuplicatePokemon)
                    {
                        await TransferDuplicatePokemonTask.Execute(session);
                    }
                    if (session.LogicSettings.RenameAboveIv)
                    {
                        await RenamePokemonTask.Execute(session);
                    }
                }
            }
        }

        private static async Task<List<FortData>> GetPokeStops(ISession session)
        {
            var mapObjects = await session.Client.Map.GetMapObjects();

            // Wasn't sure how to make this pretty. Edit as needed.
            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts)
                .Where(
                    i =>
                        i.Type == FortType.Checkpoint &&
                        i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime() &&
                        ( // Make sure PokeStop is within max travel distance, unless it's set to 0.
                            LocationUtils.CalculateDistanceInMeters(
                                session.Settings.DefaultLatitude, session.Settings.DefaultLongitude,
                                i.Latitude, i.Longitude) < session.LogicSettings.MaxTravelDistanceInMeters) ||
                        session.LogicSettings.MaxTravelDistanceInMeters == 0
                );

            return pokeStops.ToList();
        }
    }
}