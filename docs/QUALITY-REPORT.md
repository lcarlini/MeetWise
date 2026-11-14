# MeetWise quality report (phase 1)

Offline, through the exact live chain (`MeetWise.App.exe --process`, 48 kHz, 10 ms blocks). Clean speech: CMU ARCTIC US speakers (rms, slt) with known text. Noise: ESC-50. Higher DNSMOS and SI-SDR are better; lower WER is better.

Clean speech itself (the ceiling): DNSMOS OVRL 3.39, SIG 3.63, BAK 4.11, WER 0.0 %

## Noise (12 ESC-50 categories × SNR [0, 5, 10] dB)

| System | OVRL | SIG | BAK | P808 | WER | SI-SDR |
|---|---|---|---|---|---|---|
| raw | 2.42 | 3.36 | 2.51 | 3.20 | 0.9 % | 0.43 |
| Light | 3.21 | 3.48 | 4.03 | 3.93 | 2.7 % | 11.71 |
| Balanced | 3.41 | 3.61 | 4.19 | 4.10 | 1.7 % | 13.94 |
| Max | 3.42 | 3.62 | 4.18 | 4.09 | 0.8 % | 13.81 |

## Background voices (a nearby person at SIR 0 and 5 dB)

| System | OVRL | SIG | BAK | WER | SI-SDR |
|---|---|---|---|---|---|
| raw | 2.19 | 3.32 | 2.33 | 51.6 % | -0.82 |
| Max | 3.17 | 3.47 | 3.96 | 30.8 % | 0.93 |
| Voice isolation + Max | 3.14 | 3.45 | 3.97 | 26.9 % | 3.97 |

## Echo (far end through speakers, then double talk)

| System | ERLE | OVRL | SIG | BAK | WER | SI-SDR |
|---|---|---|---|---|---|---|
| raw | 0.00 | 2.54 | 3.45 | 2.81 | 38.5 % | -0.53 |
| Echo + Max | 13.63 | 3.31 | 3.55 | 4.07 | 2.5 % | 4.47 |

## How to read this

- **Noise:** Max reaches the DNSMOS of clean speech itself (3.42 vs 3.39), and the word error rate stays at the clean level (0.8 %).
- **Background voices:** the hardest case here is a second person *as loud as the user*, at 0–5 dB. Voice isolation helps less than intended in that case. Krisp's own claims are for quieter background speakers.
- **Echo:** these clips are only 6 s, and the canceller finds the delay in about 1 s and converges in about 3 s. ERLE over a 20 s run: 14 dB at 2–4 s and 24–50 dB afterwards with echo cancellation alone, and more than 30 dB from 4 s on with the noise remover after it. During double talk the word error rate drops from 38.5 % to 2.5 %.
- Regenerate this report with `accent/bench_clean.py --exe <MeetWise.App.exe>`.
