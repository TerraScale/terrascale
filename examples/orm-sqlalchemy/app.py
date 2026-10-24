from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session, selectinload

from db import engine
from models import Author, Base, Post

Base.metadata.create_all(engine)

with Session(engine) as session:
    # Create, with a relation
    ada = Author(name="Ada", email="ada@example.com",
                 posts=[Post(title="Notes on the Analytical Engine", published=True), Post(title="Draft")])
    session.add(ada)
    session.commit()

    # Read, with a relation
    ada = session.scalars(select(Author).where(Author.email == "ada@example.com").options(selectinload(Author.posts))).one()
    print(ada.name, [p.title for p in ada.posts])
    print(session.execute(select(Post.title, Author.name).join(Post.author)).all())

    # Update
    draft = session.scalars(select(Post).where(Post.title == "Draft")).one()
    draft.published = True
    draft_id = draft.id
    session.commit()

# Transaction: both writes commit, or neither does.
with Session(engine) as session, session.begin():
    grace = Author(name="Grace", email="grace@example.com")
    session.add(grace)
    session.flush()
    session.get(Post, draft_id).author_id = grace.id

# A failed transaction writes nothing.
try:
    with Session(engine) as session, session.begin():
        session.add(Author(name="Linus", email="linus@example.com"))
        session.add(Author(name="Dup", email="ada@example.com"))  # unique violation
except (IntegrityError, ValueError) as error:
    # libsql-experimental raises ValueError with the SQLite code in the message, not a DB-API IntegrityError.
    print("rolled back:", "SQLITE_CONSTRAINT_UNIQUE" if "SQLITE_CONSTRAINT_UNIQUE" in str(error) else error)

with Session(engine) as session:
    print("authors:", session.scalars(select(Author.name)).all())
    # Delete
    grace = session.scalars(select(Author).where(Author.name == "Grace")).one()
    for post in grace.posts:
        session.delete(post)
    session.delete(grace)
    session.commit()
    print("posts left:", len(session.scalars(select(Post)).all()))
