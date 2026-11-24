#!/usr/bin/env python3
"""Regression gate for passive document-open/activation appearance changes."""
from pathlib import Path
root = Path(__file__).resolve().parents[1] / 'src/CE.Tools.Civil3D'
def source(name): return (root / name).read_text(encoding='utf-8-sig')
def method(text, signature):
    start = text.index('{', text.index(signature))
    depth = 1
    end = start + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]
guard = source('PaperSpaceTextAnnotationGuard.cs')
for event in ['Idle +=', 'DocumentCreated +=', 'DocumentActivated +=']:
    assert event not in guard, event
cogo = source('CogoPointProjectStyleCommands.cs')
assert 'private static bool _pending = true' not in cogo
assert '_pending = true' not in method(cogo, 'private static void AttachDatabase(')
assert '_pending = false' in method(cogo, 'private static void DetachDatabase(')
assert 'IsUserEditCommand' in method(cogo, 'private static void OnObjectChanged(')
universal = source('UniversalDynamicRefreshCommands.cs')
assert '_pending = false' in method(universal, 'private static void Detach(')
for name in ['OnObjectChanged', 'OnObjectErased']:
    assert 'IsUserEditCommand' in method(universal, 'private static void ' + name + '(')
for name, manager in [('CommentPresentationCommands.cs', 'internal static class CommentAutoRefreshManager'), ('SewerNetworkDynamicSequenceManager.cs', 'internal static class SewerNetworkDynamicSequenceManager')]:
    watcher = source(name).split(manager, 1)[1]
    assert '_pending = false' in method(watcher, 'private static void DetachDatabase(')
    assert 'IsUserEditCommand' in method(watcher, 'private static void OnObject')
idle = method(source('AnnotationScaleSyncCommands.cs'), 'private static void OnIdle(')
assert 'ApplyCurrentScale(' not in idle
assert 'SetSystemVariable(' not in idle
print('PASS: drawing open, activation, passive scale and navigation events do not request appearance rewrites.')
