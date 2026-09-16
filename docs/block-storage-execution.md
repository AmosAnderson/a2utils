# ProDOS block-device execution

`storageProfile: "cffa2"` mounts single ProDOS block volumes through a slot-7 CFFA
card in the pinned MAME 0.289 adapter. It supports `apple2e` (6502 CFFA firmware,
slot option `cffa202`) and `apple2ee` (65C02 firmware, `cffa2`). The required CFFA
card ROM must be present in your configured ROM directory. No card ROM is bundled.

```json
{
  "schemaVersion": 1,
  "name": "large ProDOS program",
  "emulatorPath": "/path/to/mame",
  "romDirectory": "/path/to/roms",
  "machine": "apple2ee",
  "storageProfile": "cffa2",
  "disks": [
    { "device": "hard1", "image": "boot-volume.po", "verify": true },
    { "device": "hard2", "image": "data-volume.po", "verify": true }
  ],
  "until": { "address": 768, "value": 42 },
  "memory": [{ "address": 768, "hex": "2A" }]
}
```

The profile allows `hard1`/`hard2` and optional `flop1`/`flop2` mounts, with at most
two total images. The default `floppy` profile accepts only floppy devices.
Choose `execution.diskDevice: "hard1"` (or `hard2`) in a project manifest to bind
its build to that slot in an execution suite. Larger build images are rejected
as floppy mounts during project preflight. The selected filesystem must be
ProDOS for hard-device project builds.

Inputs may be raw ProDOS block-order images (including `.po`/`.hdv`) or block-order
2IMG containers, from 280 through 65,535 blocks. Every hard-device copy is checked
through the DiskArc adapter for a valid single ProDOS filesystem before MAME is
started. CHD, partition maps, DOS-order volumes, and 2IMG files with trailing
comments/creator metadata are unsupported by this profile. The latter restriction
avoids treating metadata bytes as additional sectors in MAME's raw hard-disk
reader. Input extensions remain hints: isolated raw block copies use `.hdv`, and
2IMG copies use `.2mg`, without altering any bytes or input files.

This configuration follows the upstream
[CFFA implementation](https://github.com/mamedev/mame/blob/mame0289/src/devices/bus/a2bus/a2cffa.cpp),
[slot-card registrations](https://github.com/mamedev/mame/blob/mame0289/src/devices/bus/a2bus/cards.cpp),
and [hard-disk image reader](https://github.com/mamedev/mame/blob/mame0289/src/devices/imagedev/harddriv.cpp).
Automated process-contract tests validate arguments, image checks, and input
preservation using a test double. They do not establish that a particular OS
template boots with real MAME or that an application uses the mounted device
correctly; verify those with your actual ROMs and boot template.
