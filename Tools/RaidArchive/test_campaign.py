import contextlib
import http.server
import json
import os
import pathlib
import sqlite3
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

from common import connect, digest, dumps, collection_start
from collector import collect
from app import State, Handler


class CollectionBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.game = self.root / 'game'
        self.diag = self.game / 'BepInEx/COMET/diagnostics'
        self.diag.mkdir(parents=True)
        self.archive = self.root / 'archive'
        self.settings = dict(game=str(self.game), archive=str(self.archive),
            settleSeconds=0, minimumFreeBytes=0, intervalSeconds=30, port=8791,
            rrUrl='http://127.0.0.1:7829', collectAfterUtc='2026-11-12T19:00:00Z')
        self.running = patch('collector.game_running', return_value=False)
        self.running.start()

    def tearDown(self):
        self.running.stop()
        self.temp.cleanup()

    def journal(self, session, started, filename='20261112-190100'):
        p = self.diag / f'{filename}-{session}.jsonl'
        p.write_text('\n'.join(map(dumps, [
            dict(schema=1, session=session, stage='raid-start', utc=started),
            dict(schema=1, session=session, sequence=1, feature='A01', stage='selected',
                 profileId='p', name='Tester', realtime=2, decision='Hold'),
            dict(schema=1, session=session, stage='diagnostics-end', segments=1)
        ])) + '\n', encoding='utf-8')
        return p

    def ids(self):
        with contextlib.closing(connect(self.archive)) as c:
            return {r[0] for r in c.execute('SELECT id FROM raids')}

    def test_old_journal_with_fresh_mtime_excluded_and_new_survives_restart(self):
        old = self.journal('a'*32, '2026-11-12T19:30:00+01:00')
        self.journal('b'*32, '2026-11-12T20:01:00+01:00')
        collect(self.settings)
        self.assertEqual(self.ids(), {'comet-'+'b'*32})
        os.utime(old, None)
        collect(dict(self.settings), force=True)
        self.assertEqual(self.ids(), {'comet-'+'b'*32})

    def test_headerless_old_journal_uses_filename_not_mtime(self):
        p = self.diag / ('20261111-120000-'+'a'*32+'.jsonl')
        p.write_text(dumps(dict(stage='diagnostics-end'))+'\n', encoding='utf-8')
        collect(self.settings)
        self.assertEqual(self.ids(), set())

    def test_rr_backfill_respects_boundary_and_explicit_exclusions(self):
        rrdir = self.game / 'SPT_Runtime/user/mods/RaidReview/data'
        rrdir.mkdir(parents=True)
        with contextlib.closing(sqlite3.connect(rrdir/'raid_review_mod.db')) as rr:
            rr.execute('CREATE TABLE raid(id INTEGER,raidId TEXT,location TEXT,time TEXT,exitStatus TEXT)')
            rr.executemany('INSERT INTO raid VALUES(?,?,?,?,?)', [
                (1,'old','factory','2026-11-12T18:59:59Z','Survived'),
                (2,'excluded','factory','2026-11-12T19:01:00Z','Survived'),
                (3,'new','factory','2026-11-12T19:02:00Z','Survived')])
            rr.commit()
        self.settings['excludedRrRaids'] = ['excluded']
        collect(self.settings, force=True)
        self.assertEqual(self.ids(), {'rr-new'})

    def test_pinned_session_stays_excluded_if_timestamp_changes(self):
        self.journal('a'*32, '2026-11-12T19:01:00Z')
        self.settings['excludedSessions'] = ['a'*32]
        collect(self.settings, force=True)
        self.assertEqual(self.ids(), set())

    def test_general_logs_only_copied_after_boundary(self):
        p = self.game/'BepInEx/LogOutput.log'
        p.write_text('old launch', encoding='utf-8')
        cutoff = collection_start(self.settings)
        os.utime(p, (cutoff-60, cutoff-60))
        State(self.settings).preserve_logs()
        self.assertEqual(self.ids(), set())
        p.write_text('new launch', encoding='utf-8')
        os.utime(p, (cutoff+60, cutoff+60))
        State(self.settings).preserve_logs()
        self.assertEqual(self.ids(), {'general-logs'})

    def test_invalid_boundary_rejected(self):
        for value in ('invalid', '2026-11-12T19:00:00'):
            with self.assertRaises(ValueError):collection_start(dict(collectAfterUtc=value))

    def test_archived_view_rejects_writes_and_collection(self):
        with contextlib.closing(connect(self.archive)) as c:
            c.execute("INSERT INTO raids VALUES('old','', '{}', '{}')")
            c.commit()
            c.execute('PRAGMA wal_checkpoint(TRUNCATE)')
        before = digest(self.archive/'archive.sqlite')
        settings = dict(self.settings, readOnly=True)
        state = State(settings)
        state.start(); state.cycle(True); state.reanalyze(); state.preserve_logs()
        self.assertIsNone(state.thread)
        self.assertEqual(collect(settings)['state'], 'archived')
        with contextlib.closing(connect(self.archive, read_only=True)) as c:
            with self.assertRaises(sqlite3.OperationalError):c.execute('DELETE FROM raids')
        server = http.server.ThreadingHTTPServer(('127.0.0.1',0), Handler)
        server.state = state
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        url = 'http://127.0.0.1:'+str(server.server_port)
        try:
            with urllib.request.urlopen(url+'/api/status') as r:
                self.assertTrue(json.load(r)['readOnly'])
            for route in ('/api/collect','/api/reanalyze'):
                request = urllib.request.Request(url+route, method='POST',
                    headers={'Origin':url, 'X-Raid-Local':'1'})
                with self.assertRaises(urllib.error.HTTPError) as error:urllib.request.urlopen(request)
                self.assertEqual(error.exception.code, 403)
        finally:
            server.shutdown(); server.server_close(); thread.join()
        self.assertEqual(before, digest(self.archive/'archive.sqlite'))


if __name__ == '__main__':unittest.main()
