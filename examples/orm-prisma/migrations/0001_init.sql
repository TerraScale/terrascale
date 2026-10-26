-- CreateTable
CREATE TABLE "prisma_authors" (
    "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "name" TEXT NOT NULL,
    "email" TEXT NOT NULL
);

-- CreateTable
CREATE TABLE "prisma_posts" (
    "id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "title" TEXT NOT NULL,
    "published" BOOLEAN NOT NULL DEFAULT false,
    "author_id" INTEGER NOT NULL,
    CONSTRAINT "prisma_posts_author_id_fkey" FOREIGN KEY ("author_id") REFERENCES "prisma_authors" ("id") ON DELETE RESTRICT ON UPDATE CASCADE
);

-- CreateIndex
CREATE UNIQUE INDEX "prisma_authors_email_key" ON "prisma_authors"("email");

-- CreateIndex
CREATE INDEX "prisma_posts_author_id_idx" ON "prisma_posts"("author_id");

