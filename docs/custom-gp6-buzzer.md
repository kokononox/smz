# Custom GP6 buzzer step

`Buzzer Beep` replaces `Play Audio` in the Insert menu, left rail, and right-click Add Action menu. Existing `playAudio` rows remain readable and executable in PC mode for backward compatibility.

The buzzer is passive and is driven only by Pico `GP6` through the hardware contract in issue #32. Presets are `short`, `double`, `notification`, `warning`, `success`, `error`, `rising`, `falling`, and `custom`.

Each step also has:

- volume: 1–100%
- envelope: `sharp`, `smooth`, `fade-in`, or `fade-out`
- a live preview button that uses the connected Pico

Custom syntax is `frequency:duration,pause;frequency:duration` in Hz/ms, for example `900:150,80;1200:250`. Styled tones export as `BEEP|freq,ms,volume,envelope`; pauses export as `DELAY|ms`. The legacy `BEEP|freq,ms` form remains valid and means 100%/sharp. Valid frequencies are 30–20000 Hz.
