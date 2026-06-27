<?php

declare(strict_types=1);

namespace CompTest\Oop\Contracts;

interface Shape
{
    public function area(): float;
    public function perimeter(): float;
    public function describe(): string;
}
