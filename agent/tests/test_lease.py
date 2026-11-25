from hermesvoice_agent.lease import Cls, LeaseManager, Now, QuotaTracker, class_of, REQUEST_EXPIRY


def mgr(**kw):
    return LeaseManager([1, 2], 0, QuotaTracker(**kw))


def test_first_free_window_and_classes():
    m = mgr(); n = Now(100, 0.0)
    a = m.acquire(m.make_item(10, "injury", n), n)
    b = m.acquire(m.make_item(11, "social", n), n)
    assert (a.slot, b.slot) == (1, 2)
    m.release(a)
    c = m.acquire(m.make_item(12, "social", n), n)
    assert c.slot == 1          # first free, no affinity
    assert class_of("death") == class_of("raid_start") == class_of("mental_break") == Cls.URGENT
    assert class_of("tend") == Cls.INJURY and class_of("level_up") == Cls.AMBIENT
    assert class_of("player_talk") == Cls.PLAYER > Cls.URGENT > Cls.INJURY > Cls.SOCIAL > Cls.AMBIENT


def test_preemption_only_by_higher_class():
    m = mgr(); n = Now(100, 0.0)
    a = m.acquire(m.make_item(10, "injury", n), n)
    b = m.acquire(m.make_item(11, "social", n), n)
    assert m.acquire(m.make_item(12, "social", n), n) is None          # equal to the lowest: no preempt
    assert m.acquire(m.make_item(12, "level_up", n), n) is None        # lower: no preempt
    c = m.acquire(m.make_item(12, "death", n), n)
    assert c is not None and b.preempted and b.ended and not a.preempted   # lowest class goes first
    assert c.slot == b.slot
    d = m.acquire(m.make_item(13, "player_talk", n), n)
    assert d is not None and a.preempted and not c.preempted      # player beats injury, urgent kept
    assert m.acquire(m.make_item(14, "death", n), n) is None      # nothing below urgent left


def test_pawn_already_inhabited():
    m = mgr(); n = Now(100, 0.0)
    m.acquire(m.make_item(10, "social", n), n)
    assert m.acquire(m.make_item(10, "death", n), n) is None


def test_expiry_by_class_dual_clock():
    m = mgr(); n = Now(1000, 10.0)
    urgent = m.make_item(1, "death", n); social = m.make_item(2, "social", n)
    inj = m.make_item(3, "injury", n); player = m.make_item(4, "player_talk", n)
    assert REQUEST_EXPIRY[Cls.URGENT].real_s == 5.0 and REQUEST_EXPIRY[Cls.SOCIAL].real_s == 20.0
    assert inj.expires_tick == 1000 + 5000
    assert player.expires_tick is None and player.expires_real is None
    assert not urgent.expired(Now(1100, 14.9)) and urgent.expired(Now(1100, 15.1))      # real clock
    assert urgent.expired(Now(1700, 11.0))                                              # tick clock (paused wall)
    assert not social.expired(Now(1100, 29.9)) and social.expired(Now(1100, 30.1))
    assert inj.expired(Now(6000, 11.0)) and not inj.expired(Now(5999, 11.0))
    assert not player.expired(Now(10**9, 10**6))
    m.queue += [urgent, social, player]
    dead = m.expire(Now(1100, 16.0))
    assert [d.kind for d in dead] == ["death"] and len(m.queue) == 2
    assert m.acquire(urgent, Now(1100, 16.0)) is None                  # expired items never lease


def test_lease_idle_expiry_by_class():
    m = mgr(); n = Now(0, 0.0)
    l1 = m.acquire(m.make_item(1, "level_up", n), n)
    l2 = m.acquire(m.make_item(2, "player_talk", n), n)
    out = m.expire_leases(Now(0, 20.0))
    assert out == [l1] and l2 in m.leases.values()


def test_quotas_pair_pawn_and_bypass():
    m = mgr(pawn_per_day=2, pair_per_day=1, min_gap_ticks=0)
    n = Now(100, 0.0)
    m.quotas.record(1, 2, n)
    assert m.submit(m.make_item(1, "social", n, listener=2), n) == "drop:pair_quota"
    assert m.submit(m.make_item(2, "social", n, listener=1), n) == "drop:pair_quota"   # unordered pair
    m.quotas.record(1, 3, n)
    assert m.submit(m.make_item(1, "level_up", n), n) == "drop:pawn_quota"
    assert m.submit(m.make_item(1, "death", n), n) == "queued"                          # urgent bypasses
    assert m.submit(m.make_item(1, "social", Now(100 + 60000, 0)), Now(100 + 60000, 0)) == "queued"  # next day


def test_gap_needs_both_clocks_too_soon():
    m = mgr(min_gap_ticks=1500, min_gap_real_s=20)
    m.quotas.record(1, None, Now(100, 0.0))
    assert m.quotas.allowed(1, None, Cls.AMBIENT, Now(200, 5.0)) == (False, "gap")
    assert m.quotas.allowed(1, None, Cls.AMBIENT, Now(200, 25.0))[0]      # real seconds passed
    assert m.quotas.allowed(1, None, Cls.AMBIENT, Now(2000, 5.0))[0]      # ticks passed


def test_ambient_pause_at_speed():
    m = LeaseManager([1, 2], 0, QuotaTracker(), pause_ambient_at_speed=3)
    n = Now(0, 0.0)
    assert m.submit(m.make_item(1, "level_up", n), n, speed=3).startswith("drop")
    assert m.submit(m.make_item(1, "social", n), n, speed=4).startswith("drop")
    assert m.submit(m.make_item(1, "level_up", n), n, speed=2) == "queued"
    assert m.submit(m.make_item(2, "death", n), n, speed=4) == "queued"      # urgent still runs
    assert m.submit(m.make_item(3, "injury", n), n, speed=4) == "queued"


def test_duplicate_requests_merge():
    m = mgr(); n = Now(0, 0.0)
    assert m.submit(m.make_item(1, "injury", n), n) == "queued"
    assert m.submit(m.make_item(1, "injury", n), n).startswith("merged")
    assert len(m.queue) == 1


def test_skip_if_nothing_new():
    m = mgr()
    assert not m.nothing_new(1, "sig")
    m.mark_spoke(1, "sig")
    assert m.nothing_new(1, "sig") and not m.nothing_new(1, "other")


def test_narrator_idle_only_and_coverage():
    m = mgr(); n = Now(0, 0.0)
    assert m.narrator_may_do_idle_work()
    l1 = m.acquire(m.make_item(1, "social", n), n)
    assert not m.narrator_may_do_idle_work()            # a window is busy
    m.release(l1)
    assert m.narrator_may_do_idle_work()
    m.narrator_busy = True
    assert not m.narrator_may_do_idle_work()
    m.narrator_busy = False
    m.acquire(m.make_item(1, "player_talk", n), n); m.acquire(m.make_item(2, "death", n), n)
    assert m.narrator_should_cover(m.make_item(3, "injury", n), n)      # both busy, nothing preemptable
    assert not m.narrator_should_cover(m.make_item(4, "player_talk", n), n)   # can preempt the death? no: equal/lower
    m.queue.append(m.make_item(5, "level_up", n))
    assert not m.narrator_may_do_idle_work()
