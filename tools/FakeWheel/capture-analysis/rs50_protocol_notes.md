# RS50 Protocol Notes — from the tester's real capture

Source: `diag_69W7\Logs\logitech_hidpp.log` (4.1 MB, 08-12 session, successful connect)
and the OLED readbacks in `diag_V7TM` / `diag_MZCC` / `diag_KRH9` etc.

## Device topology (tester hardware, PID 0xC276 "RS50 Base for PC")

| Interface | UsagePage / Usage | inLen | outLen | Role |
|---|---|---|---|---|
| mi_00 | 0x0001 / 0x0004 | 31 | 0 | steering input (joystick) |
| mi_01 col01 | 0xFF43 / 0x0701 | 7 | 7 | HID++ short (report 0x10) |
| mi_01 col02 | 0xFF43 / 0x0702 | 20 | 20 | HID++ long (report 0x11) |
| mi_01 col03 | 0xFF43 / 0x0704 | 64 | 64 | HID++ very-long (report 0x12) |
| mi_02 | 0xFFFD / 0xFD01 | 64 | 64 | TrueForce stream |

## Wire format (RS50, verified on hardware)

- All host commands are SHORT HID++ reports `0x10`, 7 bytes:
  `[0x10][dev=FF][featureIndex][(fn<<4)|swId(0x0A)][p0][p1][p2]`
- The wheel ALWAYS answers with VERY LONG reports `0x12`, 64 bytes,
  broadcast on every report-id collection (`0x10`, `0x11`, `0x12`).
  Response frame: `[0x12][dev=FF][featureIndex][(fn<<4)|0x0A][payload...][zeros]`
- The app matches responses by featureIndex + fn nibble (sw-id echoed but unchecked).

## Feature indices (root fn=0 answers from the real wheel)

| index | feature | find-feature answer |
|---|---|---|
| 0x00 | 0x0000 root | - |
| 0x10 | 0x8110 force feedback | `10 00 00 00 00 00` |
| 0x12 | 0x8130 dynamic display (OLED) | `12 00 00 00 00 00` |
| 0x14 | 0x8133 dampening | `14 00 00 00 00 00` |
| 0x16 | 0x8136 steering wheel (strength) | `16 00 00 00 00 00` |
| 0x17 | 0x8137 profile / mode | `17 00 00 00 00 00` |
| 0x18 | 0x8138 rotation range | `18 00 00 00 00 00` |
| 0x19 | 0x8139 TrueForce | `19 00 00 00 00 00` |

`type` and `version` bytes are 0x00. Unknown feature ids → index 0x00.

## Setting read-backs (fn=1) — exact response payloads

| feature | fn=1 payload | decode |
|---|---|---|
| 0x8136 | `FF FF 00 00 00 00` at 8 Nm | Nm × 8191.875, **little-endian** |
| 0x8137 | `00 01 00 00 00 00` (tester: desktop) | param[0] = 0 desktop, 1-5 onboard, 0xFF unknown |
| 0x8138 | `04 38 00 00 00 00` | degrees, **big-endian** (0x0438 = 1080) |
| 0x8139 | `00 00 00 00 00 00` | level %, × 655.35, little-endian |
| 0x8133 | `00 00 00 00 00 00` | damping %, single byte in param[0] |
| 0x8130 fn0 | `0A ...` | 10 layouts |
| 0x8130 fn1 | `09 0A 13 0A 13 0A ...` | layout J descriptor |

## Settings SETs (fn=2, always acked with zeros)

- 0x8136 fn2: strength, value = Nm × 8191.875 (LE bytes)
- 0x8137 fn2: `[0,0,0]` = desktop mode, `[slot,0,0]` = onboard slot 1-5
- 0x8138 fn2: degrees BE (90–2700)
- 0x8139 fn3: TrueForce level, × 655.35
- 0x8133 fn1: damping (fn1 doubles as the set for damping on this wheel)

## Behavioral notes

- The wheel boots in ONBOARD mode; live host SETs are silently IGNORED while
  onboard — only desktop mode accepts them. FFB strength defaults to 5 Nm.
- Responses are re-broadcast continuously (the wheel's 500 Hz state loop),
  which is why reads repeat dozens of times in the log.
- OLED fn3 frames ride the 0x12 collection as 64-byte reports (frame = layout
  byte + space-padded fields); the wheel does not answer them when the TrueForce
  stream is active (fire-and-forget).
- HID++ response handling is strictly single-in-flight on the wheel side.

## Fake wheel boot state (fake_rs50 defaults)

strength 8.0 Nm (0xFFFF), rotation 1080°, TrueForce 0%, damping 0%,
desktop mode (0).