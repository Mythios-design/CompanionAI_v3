# Strip the LLM features from the fork

A prompt for a future session that removes the LLM combat AI and Machine Spirit, leaving only the combat companion AI. Paste the block below into a new session.

```text
I want to strip unused features out of my fork of CompanionAI_v3 (a Warhammer 40K Rogue Trader companion AI mod) so it only contains the combat companion AI. Repo: C:\Users\paris\Documents\Scripts\40k repo, branch master, fork remote "origin" = Mythios-design/CompanionAI_v3.

Ground rules:
- Never push to the upstream author's repo. I won't be merging upstream changes, so diverging from it is fine.
- Never use em dashes, in code comments, commit messages or replies.
- Discuss before changing; commit only after I approve each stage.
- CLAUDE.md is by the original author, written in Korean; its paths are for his machine. Follow its code rules (logging via Log.<Category>, no new version marker comments, no silent catch blocks).
- Build (PowerShell; refresh PATH first: $env:Path = Machine + User):
  dotnet build CompanionAI_v3.csproj -c Release -nologo -v:minimal "-p:GamePath=D:\SteamLibrary\steamapps\common\Warhammer 40%2C000 Rogue Trader" "-p:OutputPath=C:\Users\paris\Documents\Scripts\40k build\\"
- Install only when I say the game is closed: back up the current DLL in "40k build", then copy the build to C:\Users\paris\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\companion AI\CompanionAI_v3.dll
- Commits: git -c user.name=Claude -c user.email=noreply@anthropic.com, ending with the trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

What to remove:
1. The LLM combat AI: Planning/LLM (18 files, ~5,600 lines) plus its UI (UI/LLMCombatPanel.cs, UI/LLMVisualOverlay.cs, LLM parts of UI/DecisionOverlayUI.cs) and Diagnostics/TrainingDataCollector.cs if it only serves the LLM.
2. Machine Spirit, the LLM chat companion: MachineSpirit/ (10 files, ~6,000 lines, including Knowledge/), its settings (MachineSpiritConfig in ModSettings), its UI tab and Localization keys, and Log.MachineSpirit if nothing else uses it.

Order matters: the LLM combat code depends on Machine Spirit (it uses MachineSpirit.CoroutineRunner, MachineSpirit.Knowledge and Settings.MachineSpirit.ApiUrl). Remove the LLM combat AI first (stage 1), then Machine Spirit (stage 2). If anything non-LLM uses CoroutineRunner (Main.cs does, for AbilityEffectCache, which is LLM), keep or move it rather than breaking that code.

Known hooks outside those folders (from a grep, verify yourself):
- Core/TurnOrchestrator.cs (~48 refs: the LLM judge/scorer/commander branches in ProcessTurn, ScorerWeights fields, IsLLMJudgeEnabled, ResetLLMJudgeState, GameEventCollector.AddTurnPlanSummary calls)
- UI/MainUI.cs (~46), Settings/Localization.cs (~30 keys), Settings/ModSettings.cs (EnableLLMCombatAI and related, MachineSpirit config)
- GameInterface/TurnEventHandler.cs (LLMPreCompute, LLMScorer/Cache resets, LLMCombatPanel, LLMVisualOverlay)
- GameInterface/MainAIPatch.cs (GameEventCollector clears), Main.cs (TacticalMemory, AbilityEffectCache, MSController init/shutdown, Application.quitting hook for Ollama)
- Core/TeamBlackboard.cs (CommanderDirective, TacticalMemoryContext), Core/StrategicContextKeys.cs (LLM_ScorerWeights)
- Analysis/TargetScorer.cs, Analysis/UtilityScorer.cs, Planning/Planners/MovementPlanner.cs (ScorerWeights reads; these only apply when the LLM is on, so removing them should not change behaviour)

Keep: everything else, including the companion barks (Data/CompanionDialogue, Diagnostics/TacticalNarrator, Diagnostics/TacticalDialogueDB, Dialogue/*.json), which don't use an LLM. Ability names containing "MachineSpirit" in Data/AbilityDatabase.cs are game abilities (Machine Spirit Banishment etc.), not this feature: keep them.

Make sure settings.json files that still contain the old LLM / MachineSpirit keys load without errors after the fields are removed.

How to work:
- Start by verifying the hook list and showing me the plan for stage 1. Then make the stage 1 changes, build to 0 errors, and tell me what was removed (files, approximate line count) and anything you weren't sure about.
- After I approve, commit stage 1. I'll install it and run a test fight; you check GameLogFull.txt (in the LocalLow folder above, strip CR before reading) for errors and confirm combat behaves as before.
- Then do stage 2 the same way.
- If removing something would change combat behaviour, stop and ask.
```
