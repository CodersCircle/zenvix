# Hostix AI Agent Guidelines

## Critical Restrictions
- **DO NOT** modify any items or logic in the **Dashboard** section.
- **DO NOT** modify any items or logic in the **Services** section.
- These sections are considered stable and should only be touched if explicitly requested by the USER.

## Code Safety & Reversion
- Before making significant changes to core files, ensure there is a clear plan for rollback.
- If a change causes a regression, prioritize restoring the previous working state before attempting further fixes.

## Focus Area
- Current development focus is on the **Websites** management system and project hosting orchestration.
- Any changes to infrastructure (Nginx/Apache config generators) must be carefully validated to ensure they don't break existing service stability.
