using Inngest.Connect;

namespace Inngest.Tests.Connect;

// The lease of a running request, as the extension loop, the answers of the gateway and renewals from a request sent
// again change it from different threads. Each change is one swap of the current lease, so no interleaving of them can
// pair a lease id with an answer about another lease.
public sealed class RequestLeaseTests
{
    [Fact]
    public void An_answer_that_extends_the_lease_the_extension_named_replaces_it()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);

        lease.TryExtend("lease-2").ShouldBeTrue();

        lease.Current!.Id.ShouldBe("lease-2");
    }

    [Fact]
    public void An_extension_that_read_the_lease_after_a_renewal_has_its_answer_applied()
    {
        var lease = new RequestLease("lease-1");
        lease.Renew("lease-9");

        var named = lease.Current!;
        lease.ExtensionSent(named);

        named.Id.ShouldBe("lease-9");
        lease.TryExtend("lease-10").ShouldBeTrue();
        lease.Current!.Id.ShouldBe("lease-10");
    }

    [Fact]
    public void A_grant_for_a_lease_renewed_after_the_extension_went_out_is_ignored()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);
        lease.Renew("lease-9");

        lease.TryExtend("lease-2").ShouldBeFalse();

        lease.Current!.Id.ShouldBe("lease-9");
    }

    [Fact]
    public void A_refusal_for_a_lease_renewed_after_the_extension_went_out_does_not_lose_it()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);
        lease.Renew("lease-9");

        lease.TryLose().ShouldBeFalse();

        lease.Current!.Id.ShouldBe("lease-9");
    }

    [Fact]
    public void A_refusal_of_the_lease_the_extension_named_loses_it()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);

        lease.TryLose().ShouldBeTrue();

        lease.Current.ShouldBeNull();
        lease.TryExtend("lease-2").ShouldBeFalse();
    }

    [Fact]
    public void A_renewal_to_the_same_id_is_a_new_lease()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);
        lease.Renew("lease-1");

        lease.TryLose().ShouldBeFalse();

        lease.Current!.Id.ShouldBe("lease-1");
    }

    [Fact]
    public void An_answer_before_any_extension_is_about_the_lease_the_request_came_with()
    {
        var lease = new RequestLease("lease-1");

        lease.TryLose().ShouldBeTrue();

        lease.Current.ShouldBeNull();
    }

    [Fact]
    public void An_answer_before_any_extension_is_ignored_once_the_lease_was_renewed()
    {
        var lease = new RequestLease("lease-1");
        lease.Renew("lease-9");

        lease.TryExtend("lease-2").ShouldBeFalse();
        lease.TryLose().ShouldBeFalse();

        lease.Current!.Id.ShouldBe("lease-9");
    }

    [Fact]
    public void A_released_lease_is_neither_renewed_nor_extended()
    {
        var lease = new RequestLease("lease-1");
        lease.ExtensionSent(lease.Current!);
        lease.Release();

        lease.Renew("lease-9");
        lease.TryExtend("lease-2").ShouldBeFalse();

        lease.Current.ShouldBeNull();
    }
}
