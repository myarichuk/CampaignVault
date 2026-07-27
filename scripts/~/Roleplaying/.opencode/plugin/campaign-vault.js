import { checkPressure, tryParseToolOutput } from "./pressure.js";
import { formatStatusBar } from "./statusbar.js";
import { checkDiceRollAttempt } from "./diceGuard.js";
import { CampaignInfoCache, extractCampaignInfo, buildReinjectionText } from "./campaignReinject.js";
// MCP tool names arrive prefixed by the server id configured in opencode.json (e.g. "campaign-vault"),
// so match on suffix rather than exact name. Confirm the actual prefix against a real opencode log line
// on first manual run and adjust here if it differs (e.g. dotted "campaign-vault.take_turn").
const WORLD_STATE_TOOL_SUFFIXES = [
    "start_session",
    "take_turn",
    "get_entity",
    "search_world",
    "advance_world",
    "combat",
];
function matchesWorldStateTool(tool) {
    return WORLD_STATE_TOOL_SUFFIXES.some((suffix) => tool === suffix || tool.endsWith(`_${suffix}`) || tool.endsWith(`.${suffix}`));
}
function isStartSessionTool(tool) {
    return tool === "start_session" || tool.endsWith("_start_session") || tool.endsWith(".start_session");
}
export const CampaignVaultPlugin = async ({ client }) => {
    const campaignInfoBySession = new CampaignInfoCache();
    return {
        "tool.execute.before": async ({ tool }, output) => {
            const command = typeof output.args?.command === "string" ? output.args.command : undefined;
            const guard = checkDiceRollAttempt(tool, command);
            if (guard.blocked) {
                throw new Error(guard.reason);
            }
        },
        "tool.execute.after": async ({ tool, sessionID }, output) => {
            if (!matchesWorldStateTool(tool))
                return;
            const parsed = tryParseToolOutput(output.output);
            if (parsed === null)
                return;
            if (isStartSessionTool(tool)) {
                const info = extractCampaignInfo(parsed);
                if (info)
                    campaignInfoBySession.set(sessionID, info);
            }
            const statusBar = formatStatusBar(parsed);
            if (statusBar) {
                output.output = `${statusBar}\n\n${output.output}`;
            }
            const pressure = checkPressure(parsed);
            if (pressure.engineWarnings.length > 0 || pressure.capHit) {
                const summary = pressure.engineWarnings[0]?.text ?? "Pressure escalation cap reached — resolve outstanding items now.";
                try {
                    await client.tui.showToast({
                        body: {
                            title: "ENGINE WARNING",
                            message: summary,
                            variant: "error",
                        },
                    });
                }
                catch {
                    // Toast delivery is best-effort — the pre-rendered tool output above is the source of truth.
                }
            }
        },
        event: async ({ event }) => {
            if (event.type !== "session.idle" && event.type !== "session.compacted")
                return;
            const sessionID = event.properties.sessionID;
            const info = campaignInfoBySession.get(sessionID);
            if (!info)
                return;
            try {
                await client.session.prompt({
                    path: { id: sessionID },
                    body: {
                        noReply: true,
                        parts: [{ type: "text", text: buildReinjectionText(info) }],
                    },
                });
            }
            catch {
                // Best-effort reinjection; a failed HTTP call here shouldn't crash the plugin.
            }
        },
    };
};
export default CampaignVaultPlugin;
