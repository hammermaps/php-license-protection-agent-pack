<?php

declare(strict_types=1);

namespace CompTest\DateTime;

final class DateTimeTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // DateTimeImmutable basics
        $base = new \DateTimeImmutable('2026-06-27 12:00:00', new \DateTimeZone('UTC'));
        $r['create_utc']    = $base->format('Y-m-d') === '2026-06-27';
        $r['format_time']   = $base->format('H:i:s') === '12:00:00';
        $r['timestamp']     = $base->getTimestamp() > 0;

        // Immutability — modify returns new object
        $later = $base->modify('+1 hour');
        $r['immutable']     = $base->format('H') === '12' && $later->format('H') === '13';

        // DateInterval arithmetic
        $interval = new \DateInterval('P1Y2M3DT4H5M6S');
        $r['interval_y']    = $interval->y === 1;
        $r['interval_m']    = $interval->m === 2;
        $r['interval_days'] = $interval->d === 3;

        $added = $base->add($interval);
        $r['add_interval']  = $added->format('Y') === '2027';

        $subbed = $base->sub(new \DateInterval('P30D'));
        $r['sub_interval']  = $subbed->format('Y-m-d') === '2026-05-28';

        // diff between two dates
        $a = new \DateTimeImmutable('2026-01-01');
        $b = new \DateTimeImmutable('2026-12-31');
        $diff = $a->diff($b);
        $r['diff_days']     = $diff->days === 364;
        $r['diff_invert']   = $diff->invert === 0;

        // Timezone conversion
        $utc    = new \DateTimeImmutable('2026-06-27 00:00:00', new \DateTimeZone('UTC'));
        $berlin = $utc->setTimezone(new \DateTimeZone('Europe/Berlin'));
        $r['tz_convert']    = (int)$berlin->format('H') >= 1;  // UTC+1 or +2 (CEST)

        // setDate / setTime
        $dt = (new \DateTimeImmutable())->setDate(2026, 3, 15)->setTime(9, 30, 0);
        $r['setdate']       = $dt->format('Y-m-d H:i:s') === '2026-03-15 09:30:00';

        // date_create_from_format
        $parsed = \DateTimeImmutable::createFromFormat('d.m.Y', '27.06.2026', new \DateTimeZone('UTC'));
        $r['create_from_format'] = $parsed instanceof \DateTimeImmutable && $parsed->format('Y') === '2026';

        // Timestamp round-trip
        $ts  = 1_751_030_400;
        $dt2 = (new \DateTimeImmutable())->setTimestamp($ts);
        $r['timestamp_roundtrip'] = $dt2->getTimestamp() === $ts;

        // Period iteration — recurrences=2 yields start + 2 more = 3 dates total
        $start  = new \DateTimeImmutable('2026-01-01');
        $period = new \DatePeriod($start, new \DateInterval('P1M'), 2);
        $months = [];
        foreach ($period as $d) {
            $months[] = (int)$d->format('m');
        }
        $r['date_period']   = $months === [1, 2, 3];

        // strtotime
        $ts2 = strtotime('2026-06-27 12:00:00 UTC');
        $r['strtotime']     = $ts2 > 0;

        // microtime resolution
        $t1 = microtime(true);
        usleep(1000);
        $t2 = microtime(true);
        $r['microtime']     = $t2 > $t1;

        return $r;
    }
}
