#!/bin/bash

# Pack Claude skills for Grok Web
# Grok Web now supports markdown files with YAML frontmatter (same format as Claude skills)
# Creates:
#   - grok-skills-TIMESTAMP/ folder with timestamped .md files (keeps YAML frontmatter)
#   - grok-skills-TIMESTAMP.json (index for reference)

SKILLS_DIR="./claude_skills"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
OUTPUT_DIR="grok-skills-${TIMESTAMP}"
INDEX_FILE="grok-skills-${TIMESTAMP}.json"

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Process each skill folder
python3 << PYSCRIPT
import os
import json
import glob
from datetime import datetime

output_dir = "${OUTPUT_DIR}"
timestamp = "${TIMESTAMP}"
skills_dir = "${SKILLS_DIR}"

skills_list = []

for skill_dir in sorted(glob.glob(f"{skills_dir}/*/")):
    skill_name = os.path.basename(skill_dir.rstrip("/"))
    skill_file = os.path.join(skill_dir, "SKILL.md")

    if not os.path.isfile(skill_file):
        print(f"⚠️  No SKILL.md found in {skill_dir}, skipping...")
        continue

    # Read original skill file (with YAML frontmatter)
    with open(skill_file, 'r') as f:
        content = f.read()

    # Extract name and description from frontmatter for indexing
    lines = content.split('\n')
    name = ""
    description = ""
    for line in lines:
        if line.startswith("name:"):
            name = line.replace("name:", "").strip()
        elif line.startswith("description:"):
            description = line.replace("description:", "").strip()

    # Create unique filename with timestamp (for drag/drop upload)
    output_file = os.path.join(output_dir, f"{timestamp}_{name}.md")

    # Copy skill file as-is (preserves YAML frontmatter)
    with open(output_file, 'w') as f:
        f.write(content)

    print(f"✓ Packed: {name} → {output_file}")

    skills_list.append({
        "id": name,
        "file": os.path.basename(output_file),
        "description": description,
        "sizeBytes": os.path.getsize(output_file)
    })

# Write JSON index
index = {
    "packDate": datetime.now().isoformat(),
    "packVersion": "1.0",
    "packTimestamp": timestamp,
    "folder": output_dir,
    "format": "markdown-with-yaml-frontmatter",
    "skills": skills_list
}

with open("${INDEX_FILE}", 'w') as f:
    json.dump(index, f, indent=2)

print(f"\n📦 Packed {len(skills_list)} skills to {output_dir}/")
print(f"📋 Index saved to ${INDEX_FILE}")
print(f"\n📤 To upload to Grok Web:")
print(f"   1. Drag all .md files from {output_dir}/ into Grok Web (or the upload interface)")
print(f"   2. Grok Web will parse YAML frontmatter automatically")
print(f"   3. Files are timestamped for unique naming across uploads")
PYSCRIPT
