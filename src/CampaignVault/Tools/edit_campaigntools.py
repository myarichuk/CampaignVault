import sys

file_path = r'C:\Users\myarichuk\source\repos\CampaignVault\src\CampaignVault\Tools\CampaignTools.cs'

with open(file_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Ranges to remove (1-indexed, inclusive)
# 21-22: constants
# 160-314: GetWorldState and GetScene
# 486-632: GetNpcContext, GetParty, SearchWorld, RecallHistory
# 640-676: GetNpcNeeds

ranges_to_remove = [
    (21, 22),
    (160, 314),
    (486, 632),
    (640, 676)
]

new_lines = []
for i, line in enumerate(lines, 1):
    skip = False
    for start, end in ranges_to_remove:
        if start <= i <= end:
            skip = True
            break
    if not skip:
        # replace EventGroupingKey with ExplorationTools.EventGroupingKey in AdvanceWorld
        if i == 471:
            line = line.replace('EventGroupingKey', 'ExplorationTools.EventGroupingKey')
        new_lines.append(line)

with open(file_path, 'w', encoding='utf-8') as f:
    f.writelines(new_lines)

print("File updated successfully.")
