# CE_SEWALIGN Civil 3D 2023 validation checklist

## Preparation

- Use a copy of a Civil 3D drawing containing an editable gravity sewer network.
- Run `CE_SEWSEQ` in `EntireNetwork` mode first so branch pipes use names such as `P1.1`, `P1.2`, `P2.1` and descriptions such as `Branch-1`.
- Confirm the drawing contains at least one alignment style and one alignment label-set style.

## Basic branch alignment creation

- Run `CE_SEWALIGN`.
- Select one pipe or structure from the sequenced network.
- Confirm the preview lists the network and every detected branch.
- Confirm the default confirmation is `No` and pressing Enter creates nothing.
- Run again and confirm `Yes`.
- Confirm one siteless Civil 3D alignment is created for every branch containing pipes.
- Confirm the alignment direction runs from the higher rim/cover endpoint toward the lower rim/cover endpoint.
- Confirm straight pipes create straight alignment geometry.
- Confirm curved pipes are represented by a smooth sampled plan path.
- Confirm generated objects are placed on layer `CE-SEWER-ALIGNMENT`.

## Names and drawing display

- Confirm the first available branch alignment is named `Branch-1`, `Branch-2`, and so on.
- Where an unrelated alignment already uses a branch name, confirm CE Tools uses a network-qualified alignment name instead of overwriting the unrelated alignment.
- Confirm visible plan text displays `Branch-1`, `Branch-2`, and so on near the midpoint of each branch.
- Confirm the label text remains readable against the drawing using its background mask.

## Refresh and duplicate prevention

- Move one or more sewer structures or pipes.
- Run `CE_SEWALIGN` again for the same network.
- Confirm the CE-generated alignments and branch labels are replaced rather than duplicated.
- Confirm unrelated user-created alignments and text are not erased.
- Confirm one `UNDO` reverses the complete refresh operation.

## Multiple networks and safety

- Select parts from two sequenced networks and confirm both are processed.
- Confirm duplicate branch names across separate networks receive unique alignment names.
- Select unsupported AutoCAD objects together with valid network parts and confirm they are reported as ignored.
- Select a referenced network and confirm no local alignment is created.
- Test a branch with disconnected, looped or ambiguous branch-pipe geometry and confirm the command stops without a partial commit.
- Lock layer `CE-SEWER-ALIGNMENT` and confirm the command stops without changing the drawing.

## Ribbon

- Open `CE Tools > Utilities > Pipe Network Tools`.
- Confirm both `Sewer Network Sequencing` and `Create / Refresh Branch Alignments` appear in the dropdown.
- Confirm the direct command `CE_SEWALIGN` remains available at the command line.
