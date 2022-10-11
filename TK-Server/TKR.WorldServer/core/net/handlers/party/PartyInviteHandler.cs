﻿using TKR.Shared;
using TKR.Shared.database;
using System.Collections.Generic;
using System.Linq;
using TKR.WorldServer.core.miscfile.thread;
using TKR.WorldServer.networking;
using TKR.WorldServer.networking.packets.outgoing;
using TKR.Shared.database.party;
using TKR.WorldServer.networking.packets.outgoing.party;

namespace TKR.WorldServer.core.net.handlers.party
{
    internal class PartyInviteHandler : IMessageHandler
    {
        public override MessageId MessageId => MessageId.PARTY_INVITE;

        public override void Handle(Client client, NReader rdr, ref TickTime time)
        {
            var name = rdr.ReadUTF();


            var db = client.Account.Database;

            client.Account.Reload("partyId");

            var party = DbPartySystem.Get(db, client.Account.PartyId);

            if (party == null)
            {
                var nextId = DbPartySystem.NextId(db);

                party = new DbPartySystem(db, nextId)
                {
                    PartyId = nextId,
                    PartyLeader = (client.Account.Name, client.Account.AccountId),
                    PartyMembers = new List<DbPartyMemberData>(DbPartySystem.ReturnSize(client.Rank.Rank))
                };
                party.Flush();

                client.Account.PartyId = party.PartyId;
                client.Account.FlushAsync();
                client.Account.Reload("partyId");
                client.Player?.SendInfo("Party Created Successfully!");
            }

            var target = client.GameServer.ConnectionManager.Clients
                .Keys.Where(_ => _ != null && _.Account != null && _.Account.Name.Equals(name))
                .FirstOrDefault();
            if (target == null || target.Account == null || target.Player == null)
            {
                client.Player?.SendError("Player not found.");
                return;
            }

            if (!target.Player.NameChosen)
            {
                client.Player.SendError("Player needs to choose a name first.");
                return;
            }

            target.Account.Reload("partyId");

            if (target.Account.PartyId == client.Account.PartyId)
            {
                client.Player.SendError("He's already in your Party!");
                client.Account.Reload("partyId");
                return;
            }

            if (target.Account.PartyId != 0)
            {
                client.Player.SendError("He is already in a Party!");
                client.Account.Reload("partyId");
                return;
            }

            target.SendMessage(new InvitedToParty()
            {
                Name = client.Account.Name,
                PartyId = client.Account.PartyId
            });

            client.Player.SendInfo($"Invited {target.Account.Name} to your Party!");
        }
    }
}
