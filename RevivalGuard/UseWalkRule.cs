namespace RevivalGuard
{
    /// <summary>UseWalkWatchdog's rule, pure (no ACE types), so tools/nettest compiles and checks it by path.</summary>
    public static class UseWalkRule
    {
        public enum Act { None, Settle, Fail }

        /// <summary>What to do with a use whose server walk/turn (Player.MoveToParams) has waited age seconds.</summary>
        public static Act Decide(double age, bool movingTo, bool bodyMoving, bool walkable,
                                 double settleAfter, double unfootedAfter, double limit)
        {
            if (age >= limit) return Act.Fail;
            if (!movingTo && !bodyMoving && age >= settleAfter) return Act.Settle;
            if (movingTo && !walkable && age >= unfootedAfter) return Act.Fail;
            return Act.None;
        }

        /// <summary>As above, and a cancelled move whose body is still moving or animating is answered once cancelledAfter has passed.</summary>
        public static Act Decide(double age, bool movingTo, bool bodyMoving, bool walkable,
                                 double settleAfter, double unfootedAfter, double limit, double cancelledAfter)
        {
            var act = Decide(age, movingTo, bodyMoving, walkable, settleAfter, unfootedAfter, limit);
            if (act == Act.None && !movingTo && bodyMoving && age >= cancelledAfter) return Act.Settle;
            return act;
        }
    }
}
