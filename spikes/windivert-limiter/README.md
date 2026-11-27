# Spike: WinDivert per-application download limiter (ticket #7)

Throwaway. Answers four questions: does the WinDivert driver load on current Windows 11, can we map
traffic to an application, can we hold a download to a set speed, and what happens to traffic when
the tool dies. Not production code; none of it is reused as-is.

**Run only in the Hyper-V test VM.**

## Run it (in the VM)
1. `git clone https://github.com/KareemAllam/floodgate; cd floodgate; git checkout spike/windivert-limiter; cd spikes/windivert-limiter`
2. `.\setup.ps1`  (downloads WinDivert, builds). If the build prints errors, send them to Claude: the code was not compile-checked.
3. Open PowerShell **as Administrator** in this folder.
4. `.\run.ps1`  and read the first RESULT line.
5. Only after it says both handles opened, start a big download in Edge (so it begins after the tool).
6. Let it run ~60 s, press Ctrl+C. Copy the whole output, plus the `spike-*.csv` in `out\`.
7. Crash test: repeat with `.\run.ps1 --crash-after 30` and note whether the download speeds up after the kill.

If you see error 577 or 1275 on step 4, that is the driver-signing failure, and it is a valid result.
