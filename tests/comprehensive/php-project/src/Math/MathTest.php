<?php

declare(strict_types=1);

namespace CompTest\Math;

final class MathTest
{
    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // Integer arithmetic
        $r['intdiv']      = intdiv(17, 3) === 5;
        $r['modulo']      = 17 % 3 === 2;
        $r['abs_neg']     = abs(-42) === 42;
        $r['php_int_max'] = PHP_INT_MAX === 9_223_372_036_854_775_807;

        // Float precision
        $r['floor']       = floor(4.9) === 4.0;
        $r['ceil']        = ceil(4.1) === 5.0;
        $r['round_half']  = round(2.5) === 3.0;
        $r['round_prec']  = round(M_PI, 4) === 3.1416;
        $r['fmod']        = abs(fmod(10.5, 3.2) - 0.9) < 1e-9;
        $r['fdiv_inf']    = is_infinite(fdiv(1.0, 0.0));
        $r['fdiv_nan']    = is_nan(fdiv(0.0, 0.0));
        $r['float_eps']   = PHP_FLOAT_EPSILON < 2.3e-16;

        // Trigonometry
        $r['sin_pi']      = abs(sin(M_PI)) < 1e-15;
        $r['cos_0']       = abs(cos(0) - 1.0) < 1e-15;
        $r['tan_pi4']     = abs(tan(M_PI / 4) - 1.0) < 1e-14;
        $r['atan2']       = abs(atan2(1, 1) - M_PI / 4) < 1e-14;
        $r['hypot']       = abs(hypot(3, 4) - 5.0) < 1e-14;

        // Exponents & logarithms
        $r['pow']         = 2 ** 10 === 1024;
        $r['sqrt']        = abs(sqrt(2) - M_SQRT2) < 1e-14;
        $r['exp_ln']      = abs(log(exp(1)) - 1.0) < 1e-14;
        $r['log10']       = abs(log10(1000) - 3.0) < 1e-14;
        $r['log_base']    = abs(log(8, 2) - 3.0) < 1e-14;

        // bcmath (arbitrary precision)
        if (extension_loaded('bcmath')) {
            $r['bcadd']       = bcadd('999999999999999999', '1') === '1000000000000000000';
            $r['bcmul']       = bcmul('123456789', '987654321') === '121932631112635269';
            $r['bcdiv']       = bcdiv('10', '3', 4) === '3.3333';
            $r['bcpow']       = bcpow('2', '64') === '18446744073709551616';
            $r['bcsqrt']      = bccomp(bcsqrt('2', 10), '1.4142135623', 10) === 0;
            $r['bccomp_lt']   = bccomp('1', '2') === -1;
        } else {
            $r['bcmath_skipped'] = true;
        }

        // GMP (if available)
        if (extension_loaded('gmp')) {
            $big = gmp_pow(gmp_init(2), 100);
            $r['gmp_pow']     = gmp_strval($big) === '1267650600228229401496703205376';
            $r['gmp_gcd']     = gmp_strval(gmp_gcd(48, 36)) === '12';
            $r['gmp_prime']   = gmp_prob_prime(gmp_init(997)) > 0;
        } else {
            $r['gmp_skipped'] = true;
        }

        // Bitwise
        $r['bit_and']     = (0b1010 & 0b1100) === 0b1000;
        $r['bit_or']      = (0b1010 | 0b0101) === 0b1111;
        $r['bit_xor']     = (0b1010 ^ 0b1100) === 0b0110;
        $r['bit_not']     = (~0 === -1);
        $r['bit_shl']     = (1 << 8) === 256;
        $r['bit_shr']     = (256 >> 4) === 16;

        // Random
        $rand = random_int(1, 100);
        $r['random_int_range'] = $rand >= 1 && $rand <= 100;

        $bytes = random_bytes(16);
        $r['random_bytes_len'] = strlen($bytes) === 16;

        // array_sum / array_product
        $r['array_sum']     = array_sum([1, 2, 3, 4, 5]) === 15;
        $r['array_product'] = array_product([1, 2, 3, 4, 5]) === 120;

        return $r;
    }
}
