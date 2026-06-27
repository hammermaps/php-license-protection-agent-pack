<?php

declare(strict_types=1);

namespace CompTest\Database;

final class DatabaseTest
{
    private \PDO $pdo;
    private string $dbFile;

    public function __construct(string $tmpDir)
    {
        $this->dbFile = $tmpDir . '/comptest_' . getmypid() . '.sqlite3';
        $this->pdo = new \PDO('sqlite:' . $this->dbFile);
        $this->pdo->setAttribute(\PDO::ATTR_ERRMODE, \PDO::ERRMODE_EXCEPTION);
    }

    /** @return array<string,bool> */
    public function run(): array
    {
        $r = [];

        // DDL
        $this->pdo->exec(<<<SQL
            CREATE TABLE IF NOT EXISTS products (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                price   REAL NOT NULL,
                active  INTEGER DEFAULT 1
            );
        SQL);
        $r['create_table'] = true;

        // INSERT (prepared statement)
        $stmt = $this->pdo->prepare(
            'INSERT INTO products (name, price, active) VALUES (:name, :price, :active)'
        );
        foreach ([
            ['Widget A', 9.99,  1],
            ['Widget B', 19.99, 1],
            ['Widget C', 4.99,  0],
        ] as [$name, $price, $active]) {
            $stmt->execute([':name' => $name, ':price' => $price, ':active' => $active]);
        }
        $r['insert'] = $this->pdo->lastInsertId() === '3';

        // SELECT with WHERE
        $active = $this->pdo->query(
            "SELECT COUNT(*) FROM products WHERE active = 1"
        )->fetchColumn();
        $r['select_count'] = (int)$active === 2;

        // UPDATE
        $this->pdo->exec("UPDATE products SET price = 14.99 WHERE name = 'Widget B'");
        $price = $this->pdo->query(
            "SELECT price FROM products WHERE name = 'Widget B'"
        )->fetchColumn();
        $r['update'] = (float)$price === 14.99;

        // ORDER BY + LIMIT
        $rows = $this->pdo->query(
            'SELECT name FROM products ORDER BY price DESC LIMIT 2'
        )->fetchAll(\PDO::FETCH_COLUMN);
        $r['order_limit'] = $rows[0] === 'Widget B';

        // Transaction rollback
        $this->pdo->beginTransaction();
        $this->pdo->exec("INSERT INTO products (name, price) VALUES ('Rollback', 0.01)");
        $this->pdo->rollBack();
        $cnt = (int)$this->pdo->query('SELECT COUNT(*) FROM products')->fetchColumn();
        $r['transaction_rollback'] = $cnt === 3;

        // DELETE
        $this->pdo->exec("DELETE FROM products WHERE active = 0");
        $remaining = (int)$this->pdo->query('SELECT COUNT(*) FROM products')->fetchColumn();
        $r['delete'] = $remaining === 2;

        // Aggregate
        $sum = (float)$this->pdo->query('SELECT SUM(price) FROM products')->fetchColumn();
        $r['aggregate_sum'] = abs($sum - (9.99 + 14.99)) < 0.001;

        return $r;
    }

    public function cleanup(): void
    {
        unset($this->pdo);
        if (file_exists($this->dbFile)) {
            unlink($this->dbFile);
        }
    }
}
