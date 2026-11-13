import unittest
from evidence import extra_phases

class GrenadeOutcomes(unittest.TestCase):
    def test_native_acceptance_is_not_execution(self):
        self.assertEqual(extra_phases('G01', {'accepted': True}), set())
        self.assertEqual(extra_phases('G01', {'accepted': True, 'state': 'unconfirmed'}), {'unconfirmed'})
    def test_native_start_is_not_release(self):
        self.assertEqual(extra_phases('G01', {'nativeStarted': True, 'state': 'interrupted'}), {'started'})
    def test_projectile_confirms_throw_not_hit(self):
        self.assertEqual(extra_phases('G01', {'released': True}), {'started','released','completed'})
    def test_other_features_are_not_grenade_events(self):
        self.assertEqual(extra_phases('G02', {'released': True}), set())

if __name__=='__main__':unittest.main()
