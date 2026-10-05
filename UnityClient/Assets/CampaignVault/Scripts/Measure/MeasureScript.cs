#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;

namespace CampaignVault.UnityClient.Measure
{
    /// <summary>One scripted player line.</summary>
    public sealed class MeasureBeat
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string Text;

        public MeasureBeat(string id, string label, string text) { Id = id; Label = label; Text = text; }
    }

    /// <summary>
    /// The fixed eight-beat script (scripts/measure/narration-beats.md), the same lines every run, and the scratch
    /// campaign it plays in: SFW low fantasy, a river valley with a mill town, a level 3 human ranger and a companion.
    /// </summary>
    public static class MeasureScript
    {
        public const string CampaignName = "Measure Valley";
        public const string PcId = "chars/measure-wren";
        public const string PcName = "Wren Ashdown";
        public const string StartLocationId = "locations/measure-river-road";

        public static readonly IList<MeasureBeat> Beats = new List<MeasureBeat>
        {
            new MeasureBeat("travel", "Quiet travel", "I shoulder my pack and follow the river road north toward the mill town, taking my time."),
            new MeasureBeat("search", "Search", "At the first milestone I stop and search the ditch and the stones around it for anything out of place."),
            new MeasureBeat("climb", "Failed check", "I try to climb the crumbling boundary wall to get a look over the fields."),
            new MeasureBeat("chase", "Chase with a miss", "A figure bolts from behind the wall with something of mine. I chase them and throw my hatchet at their legs."),
            new MeasureBeat("npc", "NPC exchange", "In town I find whoever keeps the mill and ask them, politely, who's been prowling the river road."),
            new MeasureBeat("rest", "Rest interrupted", "We take a room at the inn and settle in for a long rest."),
            new MeasureBeat("combat", "Combat start", "Whatever woke us, I grab my bow and go to the window to see it."),
            new MeasureBeat("lore", "Lore recall", "Once it's quiet, I think back on what I know about this valley and its old stories."),
        };

        /// <summary>The world the beats are played in, as one world_build batch. Everything here is invented for the measurement.</summary>
        public const string SeedBatch =
            "{\"locations\":["
            + "{\"id\":\"locations/measure-river-road\",\"name\":\"The River Road\",\"description\":\"A rutted wagon road following a slow brown river through barley fields, a weathered milestone at the first bend.\",\"climateZone\":\"Temperate\",\"dangerModifier\":5},"
            + "{\"id\":\"locations/measure-mill-town\",\"name\":\"Millbrook\",\"description\":\"A small town of timber and thatch around a mill pond, smoke from a dozen chimneys.\",\"connectedFromLocationId\":\"locations/measure-river-road\",\"climateZone\":\"Temperate\",\"dangerModifier\":5},"
            + "{\"id\":\"locations/measure-inn\",\"name\":\"The Gilded Eel\",\"description\":\"A low inn beside the mill pond, a hearth in the common room and four rooms above.\",\"connectedFromLocationId\":\"locations/measure-mill-town\",\"climateZone\":\"Temperate\",\"dangerModifier\":5}"
            + "],\"characters\":["
            + "{\"id\":\"chars/measure-wren\",\"name\":\"Wren Ashdown\",\"isPc\":true,\"classLevel\":\"Ranger 3\",\"currentLocationId\":\"locations/measure-river-road\","
            + "\"currentActivity\":\"Walking the river road\",\"currentAppearance\":\"A lean human in a weathered green cloak, a longbow across her back and a hatchet at her belt.\","
            + "\"systemStats\":{\"$system\":\"dnd5e\",\"level\":3,\"classLevels\":[{\"class\":\"Ranger\",\"level\":3}],\"race\":\"Human\",\"background\":\"Acolyte\",\"hitDie\":\"d10\","
            + "\"strength\":12,\"dexterity\":16,\"constitution\":14,\"intelligence\":10,\"wisdom\":15,\"charisma\":8}},"
            + "{\"id\":\"chars/measure-bram\",\"name\":\"Bram Tolliver\",\"isPartyCompanion\":true,\"classLevel\":\"Fighter 2\",\"currentLocationId\":\"locations/measure-river-road\","
            + "\"currentActivity\":\"Walking beside Wren, a pack on his back\",\"currentAppearance\":\"A broad, sunburnt man in a patched leather jerkin, a round shield slung over one shoulder.\","
            + "\"systemStats\":{\"$system\":\"dnd5e\",\"level\":2,\"classLevels\":[{\"class\":\"Fighter\",\"level\":2}],\"race\":\"Human\",\"background\":\"Acolyte\",\"hitDie\":\"d10\","
            + "\"strength\":16,\"dexterity\":12,\"constitution\":15,\"intelligence\":9,\"wisdom\":11,\"charisma\":10}},"
            + "{\"id\":\"chars/measure-hesper\",\"name\":\"Hesper Dunmore\",\"currentLocationId\":\"locations/measure-mill-town\",\"currentActivity\":\"Checking the sluice gate at the mill\","
            + "\"currentAppearance\":\"A stout woman with flour to the elbows and a ring of keys at her hip.\",\"notes\":\"The miller. Has heard wheel-ruts and voices on the river road after dark and is afraid to say so.\"}"
            + "],\"items\":["
            + "{\"id\":\"items/measure-bow\",\"name\":\"Longbow\",\"definitionName\":\"longbow\",\"holderId\":\"chars/measure-wren\",\"isEquipped\":true},"
            + "{\"id\":\"items/measure-hatchet\",\"name\":\"Handaxe\",\"definitionName\":\"handaxe\",\"holderId\":\"chars/measure-wren\",\"isEquipped\":true}"
            + "]}";
    }
}
#endif
